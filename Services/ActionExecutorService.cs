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
    private readonly LogonRegistryService _logonRegistryService;
    private readonly DeviceService _deviceService;
    private readonly IMqttManager _mqttManager;
    private readonly ILogger<ActionExecutorService> _logger;
    private readonly string _mqttTopic;

    public ActionExecutorService(
        ProcessService processService,
        WindowsService windowsService,
        LogonRegistryService logonRegistryService,
        DeviceService deviceService,
        IMqttManager mqttManager,
        ILogger<ActionExecutorService> logger)
    {
        _processService = processService;
        _windowsService = windowsService;
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
            var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString()?.ToLower();

            switch (action)
            {
                case "start_process":
                    var exe = root.GetProperty("executable").GetString();
                    var args = root.TryGetProperty("arguments", out var argProp) ? argProp.GetString() : null;
                    var elevated = root.TryGetProperty("elevated", out var elProp) ? elProp.GetBoolean() : false;
                    var asUser = root.TryGetProperty("as_user", out var usrProp) ? usrProp.GetString() : null;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        await _processService.StartProcess(exe, args, false, 30000, false, asUser, elevated);
                    }
                    break;

                case "shutdown":
                    _windowsService.Shutdown(false, false, 0, null);
                    break;

                case "reboot":
                    _windowsService.Shutdown(true, false, 0, null);
                    break;

                case "lock":
                    await _windowsService.Lock();
                    break;

                case "logoff":
                    _windowsService.Logout(false, null, 0);
                    break;

                case "login":
                    var user = root.GetProperty("username").GetString();
                    var pass = root.GetProperty("password").GetString();
                    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                    {
                        _logonRegistryService.Login(user, pass, "", false, false);
                    }
                    break;
                    
                case "device_enable":
                    var devEn = root.GetProperty("device_id").GetString();
                    if (!string.IsNullOrEmpty(devEn))
                    {
                        await _deviceService.ToggleDevices(new[] { devEn }, null);
                    }
                    break;
                    
                case "device_disable":
                    var devDis = root.GetProperty("device_id").GetString();
                    if (!string.IsNullOrEmpty(devDis))
                    {
                        await _deviceService.ToggleDevices(null, new[] { devDis });
                    }
                    break;
                    
                case "device_restart":
                    var devRes = root.GetProperty("device_id").GetString();
                    if (!string.IsNullOrEmpty(devRes))
                    {
                        await _deviceService.ToggleDevices(new[] { devRes }, new[] { devRes });
                    }
                    break;

                case "messagebox":
                    var mbTitle = root.TryGetProperty("title", out var tProp) ? tProp.GetString() : "Notification";
                    var mbMsg = root.GetProperty("message").GetString();
                    var mbSid = _processService.GetActiveConsoleSessionId();
                    var helperPath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(helperPath))
                    {
                        await _processService.StartProcess(helperPath, $"--messagebox --title \"{mbTitle}\" --message \"{mbMsg}\"", asUser: mbSid.ToString());
                    }
                    break;

                case "banner":
                    var bMsg = root.GetProperty("message").GetString();
                    var bSid = _processService.GetActiveConsoleSessionId();
                    var bannerPath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(bannerPath))
                    {
                        await _processService.StartProcess(bannerPath, $"--banner --message \"{bMsg}\"", asUser: bSid.ToString());
                    }
                    break;

                default:
                    _logger.LogWarning($"Unknown action: {action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling action payload");
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
