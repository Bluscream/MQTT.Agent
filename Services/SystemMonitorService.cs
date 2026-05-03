using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using MqttAgent.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MqttAgent.Services
{
    public class SystemMonitorService : BackgroundService
    {
        private readonly IMqttManager _mqtt;
        private readonly ILogger<SystemMonitorService> _logger;
        private readonly IDiscoveryService _discovery;
        private string _lastState = string.Empty;
        private bool _isUpdating = false;
        private EventLogWatcher? _updateWatcher;
        private ManagementEventWatcher? _arrivalWatcher;
        private ManagementEventWatcher? _removalWatcher;
        
        // Performance Counters
        private PerformanceCounter? _cpuCounter;
        private List<PerformanceCounter> _gpuCounters = new();
        private DateTime _idleStartTime = DateTime.MaxValue;
        private const int IdleThresholdSeconds = 900;
        private const float UsageThreshold = 50.0f;

        public SystemMonitorService(IMqttManager mqtt, IDiscoveryService discovery, ILogger<SystemMonitorService> logger)
        {
            _mqtt = mqtt;
            _discovery = discovery;
            _logger = logger;
            SetupUpdateMonitoring();
            SetupHardwareCounters();
            SetupDeviceMonitoring();
        }

        private void SetupUpdateMonitoring()
        {
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=43 or EventID=19)]]");
                _updateWatcher = new EventLogWatcher(query);
                _updateWatcher.EventRecordWritten += (s, e) =>
                {
                    if (e.EventRecord.Id == 43) _isUpdating = true;
                    else if (e.EventRecord.Id == 19) _isUpdating = false;
                    _logger.LogInformation("Update event detected: ID {Id}", e.EventRecord.Id);
                };
                _updateWatcher.Enabled = true;
                _logger.LogInformation("Attached EventLogWatcher to Windows Update Operational log.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not attach EventLogWatcher: {Message}", ex.Message);
            }
        }

        private void SetupDeviceMonitoring()
        {
            try
            {
                // Monitor for PnP Entity creation (plug in)
                var arrivalQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
                _arrivalWatcher = new ManagementEventWatcher(arrivalQuery);
                _arrivalWatcher.EventArrived += async (s, e) =>
                {
                    var instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                    var name = instance["Name"]?.ToString() ?? instance["Description"]?.ToString() ?? "Unknown Device";
                    await ReportRichEvent($"{name} plugged in", "device_arrival", new { 
                        device_name = name,
                        device_id = instance["DeviceID"]?.ToString()
                    });
                };
                _arrivalWatcher.Start();

                // Monitor for PnP Entity deletion (unplug)
                var removalQuery = new WqlEventQuery("SELECT * FROM __InstanceDeletionEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
                _removalWatcher = new ManagementEventWatcher(removalQuery);
                _removalWatcher.EventArrived += async (s, e) =>
                {
                    var instance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                    var name = instance["Name"]?.ToString() ?? instance["Description"]?.ToString() ?? "Unknown Device";
                    await ReportRichEvent($"{name} unplugged", "device_removal", new { 
                        device_name = name,
                        device_id = instance["DeviceID"]?.ToString()
                    });
                };
                _removalWatcher.Start();

                _logger.LogInformation("Attached PnP watchers for device changes.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not start device monitoring: {Message}", ex.Message);
            }
        }

        private async Task ReportRichEvent(string eventDescription, string eventType, object? attributes = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["event"] = eventDescription,
                ["event_type"] = eventType,
                ["timestamp"] = DateTime.UtcNow.ToString("O")
            };

            if (attributes != null)
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(attributes));
                if (dict != null)
                {
                    foreach (var kv in dict) payload[kv.Key] = kv.Value;
                }
            }

            await _mqtt.EnqueueAsync($"homeassistant/sensor/{_mqtt.UniqueId}_event/state", JsonSerializer.Serialize(payload), false);
        }

        private void SetupHardwareCounters()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                _cpuCounter.NextValue(); // First reading is always 0
                
                // Fetch GPU 3D engine counters
                var gpuCategory = new PerformanceCounterCategory("GPU Engine");
                var instances = gpuCategory.GetInstanceNames().Where(i => i.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase));
                
                foreach (var inst in instances)
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    pc.NextValue(); // First reading is always 0
                    _gpuCounters.Add(pc);
                }
                
                _logger.LogInformation("Hardware counters initialized (CPU and {Count} GPU engines).", _gpuCounters.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not initialize hardware counters: {Message}", ex.Message);
            }
        }

        public async void HandleSessionChange(int sessionId, SessionChangeReason reason)
        {
            _logger.LogInformation("Session change detected: Session {Id}, Reason {Reason}", sessionId, reason);
            
            await ReportRichEvent($"Session {reason}", "session_change", new { 
                session_id = sessionId,
                reason = reason.ToString()
            });
            
            await UpdateState();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("System Monitor Service starting...");
                
                // Wait for MQTT to be connected
                while (!_mqtt.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested) return;

                // Initial Discovery and State report
                await _discovery.PublishDiscoveryAsync();
                _lastState = "unknown";
                await _mqtt.EnqueueAsync($"homeassistant/select/{_mqtt.UniqueId}/state", "unknown", true);
                await ReportAttributes();
                
                await ReportRichEvent("Agent Started", "startup");
                
                await UpdateState();

                while (!stoppingToken.IsCancellationRequested)
                {
                    await UpdateState();
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("System Monitor Service cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System Monitor Service crashed: {Message}", ex.Message);
            }
        }

        private async Task UpdateState()
        {
            string state = "On";

            if (SystemHelper.IsSafeMode())
            {
                state = "Safe Mode";
            }
            else if (_isUpdating)
            {
                state = "Updating";
            }
            else if (SystemHelper.IsLocked())
            {
                state = "Locked";
            }
            else if (!SystemHelper.IsUserLoggedIn())
            {
                state = "Logged out";
            }
            else if (CheckIdle())
            {
                state = "Idle";
            }

            if (state == _lastState)
            {
                // Periodically update attributes anyway
                await ReportAttributes();
                return;
            }

            _logger.LogInformation("State transition: {Old} -> {New}", _lastState, state);
            _lastState = state;
            
            var uniqueId = _mqtt.UniqueId;
            var stateTopic = $"homeassistant/select/{uniqueId}/state";
            await _mqtt.EnqueueAsync(stateTopic, state, true);

            await ReportRichEvent($"State changed to {state}", "state_change", new { 
                old_state = _lastState,
                new_state = state
            });

            await ReportAttributes();
        }

        private bool CheckIdle()
        {
            if (_cpuCounter == null) return false;

            try
            {
                float cpuUsage = _cpuCounter.NextValue();
                float gpuUsage = 0;
                
                foreach (var counter in _gpuCounters)
                {
                    try { gpuUsage = Math.Max(gpuUsage, counter.NextValue()); } catch { }
                }

                if (cpuUsage < UsageThreshold && gpuUsage < UsageThreshold)
                {
                    if (_idleStartTime == DateTime.MaxValue)
                        _idleStartTime = DateTime.Now;
                    
                    var idleDuration = (DateTime.Now - _idleStartTime).TotalSeconds;
                    return idleDuration >= IdleThresholdSeconds;
                }
                else
                {
                    _idleStartTime = DateTime.MaxValue;
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task ReportAttributes()
        {
            var uniqueId = _mqtt.UniqueId;
            var attrTopic = $"homeassistant/select/{uniqueId}/attributes";
            
            var users = SystemHelper.GetLoggedInUsers();
            var attributes = new
            {
                logged_in_users = users,
                last_updated = DateTime.Now.ToString("O"),
                cpu_load = _cpuCounter?.NextValue() ?? 0,
                gpu_load = _gpuCounters.Count > 0 ? _gpuCounters.Max(c => { try { return c.NextValue(); } catch { return 0; } }) : 0,
                power_profile = PowerHelper.GetActiveScheme()
            };

            await _mqtt.EnqueueAsync(attrTopic, System.Text.Json.JsonSerializer.Serialize(attributes), true);
            
            // Also update the power profile state topic
            await _mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/state", attributes.power_profile, true);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("System Monitor Service stopping (publishing unavailable)...");
            var stateTopic = $"homeassistant/select/{_mqtt.UniqueId}/state";
            await _mqtt.EnqueueAsync(stateTopic, "unavailable", true);
            await Task.Delay(500, cancellationToken); // Give it time to flush

            _updateWatcher?.Dispose();
            _arrivalWatcher?.Dispose();
            _removalWatcher?.Dispose();
            _cpuCounter?.Dispose();
            foreach (var c in _gpuCounters) c.Dispose();
            await base.StopAsync(cancellationToken);
        }
    }
}
