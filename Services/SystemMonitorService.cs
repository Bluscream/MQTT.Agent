using System;
using MqttAgent.Utils;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;
using System.Text.Json;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LibreHardwareMonitor.Hardware;
using System.Net.Http;

namespace MqttAgent.Services
{
    public class SystemMonitorService : BackgroundService
    {
        private readonly IMqttManager _mqtt;
        private readonly ILogger<SystemMonitorService> _logger;
        private readonly IDiscoveryService _discovery;
        private string _lastState = "unknown";
        private int _updateIntervalSeconds = 60;
        private DateTime _idleStartTime = DateTime.MaxValue;
        private bool _isUpdating = false;
        private bool _isShuttingDown = false;
        private bool _isLoggingIn = false;
        private bool _isLoggingOut = false;
        private EventLogWatcher? _updateWatcher;
        private ManagementEventWatcher? _arrivalWatcher;
        private ManagementEventWatcher? _removalWatcher;
        
        // LibreHardwareMonitor
        private readonly Computer _computer;
        private const int IdleThresholdSeconds = 1800; // 30 minutes
        private const float IdleUsageThreshold = 25.0f;
        private const float ActiveUsageThreshold = 50.0f;
        private const int ActiveThresholdSeconds = 120; // 2 minutes
        private const int NeedsAttentionClearThresholdSeconds = 10;
        private DateTime _activeStartTime = DateTime.MaxValue;
        private bool _isCurrentlyIdle = false;
        private DateTime _needsAttentionClearTime = DateTime.MaxValue;
        private bool _isCurrentlyNeedsAttention = false;
        private MqttAgent.Models.NeedsAttentionInfo? _lastAttentionInfo;
        private static readonly HttpClient _httpClient = new HttpClient();
        private float? _cachedGpuTotalVramGb = null;

        public SystemMonitorService(IMqttManager mqtt, IDiscoveryService discovery, ILogger<SystemMonitorService> logger)
        {
            _mqtt = mqtt;
            _discovery = discovery;
            _logger = logger;

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true
            };
            _computer.Open();

            SetupUpdateMonitoring();
            SetupDeviceMonitoring();
            
            SystemEvents.SessionSwitch += (s, e) => HandleSessionChange(0, (SessionChangeReason)e.Reason);
            SystemEvents.SessionEnding += (s, e) => {
                if (e.Reason == SessionEndReasons.SystemShutdown) _isShuttingDown = true;
                else _isLoggingOut = true;
                _ = UpdateState();
            };
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

        public async Task ReportRichEvent(string eventDescription, string eventType, object? attributes = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["event"] = eventDescription,
                ["event_type"] = eventType,
                ["machine_name"] = Global.MachineName,
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

            bool httpSuccess = false;
            var hassServer = Environment.GetEnvironmentVariable("HASS_SERVER");
            var hassToken = Environment.GetEnvironmentVariable("HASS_TOKEN");
            
            if (!string.IsNullOrEmpty(hassServer) && !string.IsNullOrEmpty(hassToken))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{hassServer.TrimEnd('/')}/api/events/pc");
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", hassToken);
                    var payloadStr = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(payloadStr, System.Text.Encoding.UTF8, "application/json");
                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        httpSuccess = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to post event to HASS API: {Message}", ex.Message);
                }
            }

            if (!httpSuccess)
            {
                await _mqtt.EnqueueAsync($"homeassistant/sensor/{_mqtt.UniqueId}_event/state", JsonSerializer.Serialize(payload), false);
            }
        }

        private void SetupHardwareCounters() { } // Deprecated

        public async void HandleSessionChange(int sessionId, SessionChangeReason reason)
        {
            _logger.LogInformation("Session change detected: Session {Id}, Reason {Reason}", sessionId, reason);
            
            if (reason == SessionChangeReason.SessionLogon) _isLoggingIn = true;
            if (reason == SessionChangeReason.SessionLogoff) _isLoggingOut = true;

            await ReportRichEvent($"Session {reason}", "session_change", new { 
                session_id = sessionId,
                reason = reason.ToString()
            });
            
            await UpdateState();

            // Reset transient flags after update
            if (reason == SessionChangeReason.SessionLogon) _isLoggingIn = false;
            if (reason == SessionChangeReason.SessionLogoff) _isLoggingOut = false;
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

                // Subscribe to command topic
                await _mqtt.SubscribeAsync($"homeassistant/number/{_mqtt.UniqueId}_update_interval/set", e =>
                {
                    var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                    if (int.TryParse(payload, out int val) && val >= 1 && val <= 3600)
                    {
                        _updateIntervalSeconds = val;
                        _logger.LogInformation("Update interval changed to {Val}s", val);
                    }
                    return Task.CompletedTask;
                });

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
                    await Task.Delay(TimeSpan.FromSeconds(_updateIntervalSeconds), stoppingToken);
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
            bool moreStates = Global.IsMoreStatesEnabled;
            MqttAgent.Models.NeedsAttentionInfo? attentionInfo = null;

            if (SystemHelper.IsSafeMode())
            {
                state = "Safe Mode";
            }
            else if (_isShuttingDown)
            {
                state = "Shutting Down";
            }
            else if (_isLoggingIn)
            {
                state = "Logging In";
            }
            else if (_isLoggingOut)
            {
                state = "Logging Out";
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
            else if (moreStates && (attentionInfo = GetAttentionInfo()) != null)
            {
                state = "Needs attention";
            }
            else if (moreStates && CheckIdle())
            {
                state = "Idle";
            }

            if (state == _lastState && state != "Needs attention")
            {
                // Periodically update attributes anyway
                await ReportAttributes();
                return;
            }

            // If it was already "Needs attention" but the window changed, we still want a new event
            if (state == _lastState && state == "Needs attention")
            {
                if (attentionInfo?.WindowName == _lastAttentionInfo?.WindowName && attentionInfo?.ProcessName == _lastAttentionInfo?.ProcessName)
                {
                    await ReportAttributes();
                    return;
                }
            }

            _logger.LogInformation("State transition: {Old} -> {New}", _lastState, state);
            _lastState = state;
            _lastAttentionInfo = attentionInfo;
            
            var uniqueId = _mqtt.UniqueId;
            var stateTopic = $"homeassistant/select/{uniqueId}/state";
            await _mqtt.EnqueueAsync(stateTopic, state, true);

            object eventAttributes = new { 
                old_state = _lastState,
                new_state = state
            };

            if (state == "Needs attention" && attentionInfo != null)
            {
                eventAttributes = new {
                    old_state = _lastState,
                    new_state = state,
                    window_name = attentionInfo.WindowName,
                    process_name = attentionInfo.ProcessName,
                    process_id = attentionInfo.ProcessId,
                    command_line = attentionInfo.CommandLine,
                    class_name = attentionInfo.ClassName
                };
            }

            await ReportRichEvent($"State changed to {state}", "state_change", eventAttributes);

            await ReportAttributes();
        }

        private MqttAgent.Models.NeedsAttentionInfo? GetAttentionInfo()
        {
            var info = SystemHelper.GetNeedsAttentionInfo();
            if (info != null)
            {
                _isCurrentlyNeedsAttention = true;
                _needsAttentionClearTime = DateTime.MaxValue;
            }
            else if (_isCurrentlyNeedsAttention)
            {
                if (_needsAttentionClearTime == DateTime.MaxValue) _needsAttentionClearTime = DateTime.Now;
                if ((DateTime.Now - _needsAttentionClearTime).TotalSeconds >= NeedsAttentionClearThresholdSeconds)
                {
                    _isCurrentlyNeedsAttention = false;
                    _needsAttentionClearTime = DateTime.MaxValue;
                }
                else
                {
                    // Still in threshold, return last known info if possible?
                    // Actually, if info is null but we're in threshold, we might just return null and the state will flip back to "On"
                    // But the user said "CheckNeedsAttention" logic was to prevent flickering.
                    // So if we're in threshold, we should probably stay in "Needs attention" state.
                    return _lastAttentionInfo; 
                }
            }
            return info;
        }

        private bool CheckIdle()
        {
            try
            {
                foreach (var hardware in _computer.Hardware) hardware.Update();
                
                float cpuUsage = _computer.Hardware.GetMaxSensorValue(HardwareType.Cpu, SensorType.Load, "CPU Total");
                float gpuUsage = _computer.Hardware.GetMaxGpuSensorValue(SensorType.Load, "GPU Core");

                if (_isCurrentlyIdle)
                {
                    if (cpuUsage >= ActiveUsageThreshold || gpuUsage >= ActiveUsageThreshold)
                    {
                        if (_activeStartTime == DateTime.MaxValue) _activeStartTime = DateTime.Now;
                        if ((DateTime.Now - _activeStartTime).TotalSeconds >= ActiveThresholdSeconds)
                        {
                            _isCurrentlyIdle = false;
                            _idleStartTime = DateTime.MaxValue;
                        }
                    }
                    else
                    {
                        _activeStartTime = DateTime.MaxValue;
                    }
                }
                else
                {
                    if (cpuUsage < IdleUsageThreshold && gpuUsage < IdleUsageThreshold)
                    {
                        if (_idleStartTime == DateTime.MaxValue) _idleStartTime = DateTime.Now;
                        if ((DateTime.Now - _idleStartTime).TotalSeconds >= IdleThresholdSeconds)
                        {
                            _isCurrentlyIdle = true;
                            _activeStartTime = DateTime.MaxValue;
                        }
                    }
                    else
                    {
                        _idleStartTime = DateTime.MaxValue;
                    }
                }

                return _isCurrentlyIdle;
            }
            catch
            {
                return false;
            }
        }

        private string? _cachedRamStickNames = null;

        private async Task ReportAttributes()
        {
            var uniqueId = _mqtt.UniqueId;
            var attrTopic = $"homeassistant/select/{uniqueId}/attributes";

            // Collect ALL power sensor values across all hardware for total_power calculation
            var allPowerReadings = new System.Collections.Generic.List<(string source, float watts)>();
            // Store deferred publishes so we can patch CPU attrs after the loop
            var deferredPublishes = new System.Collections.Generic.List<(string typeKey, string sensorTopic, string sensorAttrTopic, string hwName, Dictionary<string, object> attrs)>();
            
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                var hwName = hardware.Name;
                if (hardware.HardwareType == HardwareType.Memory)
                {
                    if (hwName == "Virtual Memory") continue;
                    
                    if (_cachedRamStickNames != null)
                    {
                        hwName = _cachedRamStickNames;
                    }
                    else
                    {
                        try
                        {
                            using (var searcher = new ManagementObjectSearcher("SELECT PartNumber FROM Win32_PhysicalMemory"))
                            {
                                var sticks = searcher.Get()
                                    .Cast<ManagementObject>()
                                    .Select(obj => obj.GetPropertyString("PartNumber")?.Trim())
                                    .Where(s => !string.IsNullOrWhiteSpace(s))
                                    .OrderBy(s => s)
                                    .ToList();
                                
                                if (sticks.Any())
                                {
                                    _cachedRamStickNames = string.Join("\n", sticks);
                                    hwName = _cachedRamStickNames;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch RAM stick names from WMI.");
                        }
                    }
                }

                float usage = 0, temp = 0, used = 0, free = 0, total = 0, power = 0;
                bool hasData = false;
                bool isGpu = hardware.HardwareType == HardwareType.GpuNvidia || hardware.HardwareType == HardwareType.GpuAmd || hardware.HardwareType == HardwareType.GpuIntel;
                var typeKey2 = hardware.HardwareType == HardwareType.Memory ? "ram" : (hardware.HardwareType == HardwareType.Cpu ? "cpu" : "gpu");

                foreach (var s in hardware.Sensors)
                {
                    if (s.SensorType == SensorType.Load)
                    {
                        if (hardware.HardwareType == HardwareType.Cpu && s.Name == "CPU Total") { usage = s.Value ?? 0; hasData = true; }
                        else if (isGpu && s.Name == "GPU Core") { usage = s.Value ?? 0; hasData = true; }
                        else if (hardware.HardwareType == HardwareType.Memory && s.Name == "Memory") { usage = s.Value ?? 0; hasData = true; }
                    }
                    else if (s.SensorType == SensorType.Temperature)
                    {
                        if ((hardware.HardwareType == HardwareType.Cpu && (s.Name.Contains("Package") || s.Name.Contains("Core (Max)"))) ||
                            (isGpu && s.Name == "GPU Core"))
                        {
                            temp = s.Value ?? 0;
                        }
                    }
                    else if (s.SensorType == SensorType.Power)
                    {
                        // Pick the main power reading for the individual "power" attribute
                        if ((hardware.HardwareType == HardwareType.Cpu && (s.Name == "CPU Package" || s.Name == "Package")) ||
                            (isGpu && (s.Name == "GPU Core" || s.Name == "GPU Package")))
                        {
                            power = s.Value ?? 0;
                        }
                        // Track ALL power sensors for total_power summation
                        float val = s.Value ?? 0;
                        if (val > 0)
                        {
                            allPowerReadings.Add(($"{typeKey2}/{s.Name}", val));
                        }
                    }
                    else if (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData)
                    {
                        // SmallData = MB, Data = GB
                        float valGB = s.Value ?? 0;
                        if (s.SensorType == SensorType.SmallData) valGB /= 1024.0f; // Convert MB to GB

                        if (s.Name == "Memory Used" || s.Name == "GPU Memory Used" || s.Name == "D3D Dedicated Memory Used") { used = valGB; hasData = true; }
                        else if (s.Name == "Memory Available" || s.Name == "GPU Memory Free" || s.Name == "D3D Dedicated Memory Free") { free = valGB; hasData = true; }
                        else if (s.Name == "Memory Total" || s.Name == "GPU Memory Total" || s.Name == "D3D Dedicated Memory Total") { total = valGB; hasData = true; }
                    }
                }

                // If free is not reported but total and used are, derive free
                if (free <= 0 && total > 0 && used > 0)
                {
                    free = total - used;
                }
                // If total is not reported but both used and free are, derive total
                else if (total <= 0 && used > 0 && free > 0)
                {
                    total = used + free;
                }

                // For GPU: if we still don't have total/free, try WMI as fallback
                if (isGpu && used > 0 && free <= 0 && total <= 0)
                {
                    if (_cachedGpuTotalVramGb != null)
                    {
                        total = _cachedGpuTotalVramGb.Value;
                        free = total - used;
                        if (free < 0) free = 0;
                    }
                    else
                    {
                        try
                        {
                            using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController WHERE Name IS NOT NULL");
                            foreach (var obj in (ManagementObjectCollection)searcher.Get())
                            {
                                var totalBytes = obj.GetPropertyLong("AdapterRAM");
                                if (totalBytes > 0)
                                {
                                    total = totalBytes / (1024.0f * 1024.0f * 1024.0f); // bytes -> GB
                                    _cachedGpuTotalVramGb = total;
                                    free = total - used;
                                    if (free < 0) free = 0;
                                    _logger.LogDebug("GPU VRAM total from WMI: {Total:F1} GB", total);
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch GPU VRAM total from WMI.");
                        }
                    }
                }

                if (hasData || temp > 0)
                {
                    var typeKey = hardware.HardwareType == HardwareType.Memory ? "ram" : (hardware.HardwareType == HardwareType.Cpu ? "cpu" : "gpu");
                    var sensorTopic = $"homeassistant/sensor/{uniqueId}_{typeKey}/state";
                    var sensorAttrTopic = $"homeassistant/sensor/{uniqueId}_{typeKey}/attributes";

                    var attrs = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { "usage", Math.Round(usage) }
                    };
                    if (temp > 0) attrs.Add("temp", Math.Round(temp));
                    if (power > 0) attrs.Add("power", Math.Round(power, 1));
                    
                    // For RAM/GPU memory, values are now normalized to GB (numeric)
                    if (used > 0 || free > 0)
                    {
                        if (hardware.HardwareType == HardwareType.Memory)
                        {
                            if (used > 0) attrs.Add("used", Math.Round(used, 1));
                            if (free > 0) attrs.Add("free", Math.Round(free, 1));
                            if (total > 0) attrs.Add("total", Math.Round(total, 1));
                        }
                        else
                        {
                            if (used > 0) attrs.Add("vram_used", Math.Round(used, 1));
                            if (free > 0) attrs.Add("vram_free", Math.Round(free, 1));
                            if (total > 0) attrs.Add("vram_total", Math.Round(total, 1));
                            if (total > 0 && used > 0)
                            {
                                attrs.Add("vram_usage", Math.Round(used / total * 100));
                            }
                            else if (used > 0 && free > 0)
                            {
                                attrs.Add("vram_usage", Math.Round(used / (used + free) * 100));
                            }
                        }
                    }

                    // Defer publishing so we can add total_power to CPU after the loop
                    deferredPublishes.Add((typeKey, sensorTopic, sensorAttrTopic, hwName, attrs));
                }
            }

            // Add total_power to CPU attributes (sum of ALL power readings across the system)
            if (allPowerReadings.Count > 0)
            {
                float totalPower = allPowerReadings.Sum(r => r.watts);
                var cpuEntry = deferredPublishes.FirstOrDefault(p => p.typeKey == "cpu");
                if (cpuEntry.attrs != null)
                {
                    cpuEntry.attrs["total_power"] = Math.Round(totalPower, 1);
                }
            }

            // Now publish all deferred sensor data
            foreach (var (typeKey, sensorTopic, sensorAttrTopic, hwName, attrs) in deferredPublishes)
            {
                await _mqtt.EnqueueAsync(sensorTopic, hwName, true);
                await _mqtt.EnqueueAsync(sensorAttrTopic, JsonSerializer.Serialize(attrs), true);
            }

            var users = SystemHelper.GetLoggedInUsers();
            var attributes = new
            {
                power_profile = PowerHelper.GetActiveScheme(),
                users = string.Join(", ", users),
                user_count = users.Count
            };

            await _mqtt.EnqueueAsync(attrTopic, JsonSerializer.Serialize(attributes), true);
            
            // Also update the power profile state topic
            await _mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/state", attributes.power_profile, true);
            
            // Update interval state
            await _mqtt.EnqueueAsync($"homeassistant/number/{uniqueId}_update_interval/state", _updateIntervalSeconds.ToString(), true);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("System Monitor Service stopping (Shutting Down)...");
            _isShuttingDown = true;
            await UpdateState();
            
            _updateWatcher?.Dispose();
            _arrivalWatcher?.Dispose();
            _removalWatcher?.Dispose();
            _computer.Close();
            await base.StopAsync(cancellationToken);
        }
    }
}
