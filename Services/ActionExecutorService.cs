using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using MqttAgent.Utils;

namespace MqttAgent.Services;

public class ActionExecutorService : IHostedService
{
    private readonly ProcessService _processService;
    private readonly WindowsService _windowsService;
    private readonly ForceActionService _forceActionService;
    private readonly LogonRegistryService _logonRegistryService;
    private readonly DeviceService _deviceService;
    private readonly IMqttManager _mqttManager;
    private readonly ILogger<ActionExecutorService> _logger;
    private readonly string _mqttTopic;

    public ActionExecutorService(
        ProcessService processService,
        WindowsService windowsService,
        ForceActionService forceActionService,
        LogonRegistryService logonRegistryService,
        DeviceService deviceService,
        IMqttManager mqttManager,
        ILogger<ActionExecutorService> logger)
    {
        _processService = processService;
        _windowsService = windowsService;
        _forceActionService = forceActionService;
        _logonRegistryService = logonRegistryService;
        _deviceService = deviceService;
        _mqttManager = mqttManager;
        _logger = logger;
        
        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        _mqttTopic = $"homeassistant/action/{machineName}/command";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttManager.SubscribeAsync(_mqttTopic, HandleActionPayloadAsync);
        await _mqttManager.SubscribeAsync($"homeassistant/select/{_mqttManager.UniqueId}_power_profile/set", HandlePowerProfileSetAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandleActionPayloadAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            if (e.ApplicationMessage.Retain)
            {
                _logger.LogInformation("Ignoring retained action payload to prevent startup execution loop.");
                await _mqttManager.EnqueueAsync(_mqttTopic, "", true);
                return;
            }

            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment).Trim();
            string? action = null;
            JsonElement? root = null;

            if (payload.StartsWith("{") && payload.EndsWith("}"))
            {
                using var doc = JsonDocument.Parse(payload);
                root = doc.RootElement.Clone();
                action = root.Value.TryGetProperty("action", out var actionProp) ? actionProp.GetString()?.ToLower() : null;
            }
            
            if (string.IsNullOrEmpty(action))
            {
                action = payload.ToLower();
            }

            switch (action)
            {
                case "start_process":
                    if (!root.HasValue) break;
                    var exe = root.Value.GetProperty("executable").GetString();
                    var args = root.Value.TryGetProperty("arguments", out var argProp) ? argProp.GetString() : null;
                    var elevated = root.Value.TryGetProperty("elevated", out var elProp) ? elProp.GetBoolean() : false;
                    var asUser = root.Value.TryGetProperty("as_user", out var usrProp) ? usrProp.GetString() : null;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        await _processService.StartProcess(exe, args, false, 30000, false, asUser, elevated);
                    }
                    break;

                case "shutdown":
                    _windowsService.Shutdown(false, _forceActionService.IsForceEnabled, 0, null);
                    break;

                case "reboot":
                    _windowsService.Shutdown(true, _forceActionService.IsForceEnabled, 0, null);
                    break;

                case "lock":
                    await _windowsService.Lock();
                    break;

                case "logoff":
                    _windowsService.Logout(false, null, 0, _forceActionService.IsForceEnabled);
                    break;

                case "login":
                    if (!root.HasValue) break;
                    var user = root.Value.GetProperty("username").GetString();
                    var pass = root.Value.GetProperty("password").GetString();
                    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                    {
                        _logonRegistryService.Login(user, pass, "", false, false);
                    }
                    break;
                    
                case "device_enable":
                    if (!root.HasValue) break;
                    var devEn = root.Value.GetProperty("device_id").GetString();
                    if (!string.IsNullOrEmpty(devEn))
                    {
                        await _deviceService.ToggleDevices(new[] { devEn }, null);
                    }
                    break;
                    
                case "device_disable":
                    if (!root.HasValue) break;
                    var devDis = root.Value.GetProperty("device_id").GetString();
                    if (!string.IsNullOrEmpty(devDis))
                    {
                        await _deviceService.ToggleDevices(null, new[] { devDis });
                    }
                    break;
                    
                case "device_restart":
                    if (!root.HasValue) break;
                    var devRes = root.Value.GetProperty("device_id").GetString();
                    if (!string.IsNullOrEmpty(devRes))
                    {
                        await _deviceService.ToggleDevices(new[] { devRes }, new[] { devRes });
                    }
                    break;

                case "messagebox":
                    if (!root.HasValue) break;
                    var mbTitle = root.Value.TryGetProperty("title", out var tProp) ? tProp.GetString() : "Notification";
                    var mbMsg = root.Value.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "";
                    var mbSid = _processService.GetActiveConsoleSessionId();
                    var helperPath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(helperPath))
                    {
                        await _processService.StartProcess(helperPath, $"--messagebox --title \"{mbTitle}\" --message \"{mbMsg}\"", asUser: mbSid.ToString(), windowStyle: "hidden");
                    }
                    break;

                case "banner":
                    if (!root.HasValue) break;
                    var bMsg = root.Value.TryGetProperty("message", out var bMsgProp) ? bMsgProp.GetString() : "";
                    var bSid = _processService.GetActiveConsoleSessionId();
                    var bannerPath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(bannerPath))
                    {
                        await _processService.StartProcess(bannerPath, $"--banner --message \"{bMsg}\"", asUser: bSid.ToString(), windowStyle: "hidden");
                    }
                    break;

                default:
                    _logger.LogWarning($"Unknown action: {action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("blocked"))
            {
                _logger.LogWarning(ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Error handling action payload");
            }
        }
    }

    private async Task HandlePowerProfileSetAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var schemeName = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            _logger.LogInformation($"Setting active power profile to: {schemeName}");
            
            if (PowerHelper.SetActiveScheme(schemeName))
            {
                // Update state topic immediately
                await _mqttManager.EnqueueAsync($"homeassistant/select/{_mqttManager.UniqueId}_power_profile/state", schemeName, true);
            }
            else
            {
                _logger.LogWarning($"Failed to set power profile: {schemeName}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling power profile set");
        }
    }
}
