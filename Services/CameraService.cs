using System;
using MqttAgent.Utils;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MqttAgent.Models;

namespace MqttAgent.Services;

public class CameraService : BackgroundService
{
    private readonly IMqttManager _mqtt;
    private readonly ScreenshotService _screenshotService;
    private readonly TokenService _tokenService;
    private readonly string _port;
    private readonly string _topic;

    public CameraService(IMqttManager mqtt, ScreenshotService screenshotService, TokenService tokenService, ILogger<CameraService> logger)
    {
        _mqtt = mqtt;
        _screenshotService = screenshotService;
        _tokenService = tokenService;
        _logger = logger;
        _uniqueId = Global.UniqueId;
        _port = Config.Get("port", "MQTTAGENT_PORT") ?? "23482";
        _topic = $"homeassistant/camera/{_uniqueId}_desktop/image";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CameraService starting...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var screens = await _screenshotService.ListScreens();
                if (screens == null || screens.Count == 0)
                {
                    // Fallback to primary if listing fails
                    await CaptureAndPublish("all", "Desktop", stoppingToken);
                }
                else
                {
                    foreach (dynamic screen in screens)
                    {
                        string screenName = screen.name;
                        string deviceName = screen.deviceName;
                        string sanitizedName = screenName.Replace("\\\\.\\", "").ToLowerInvariant();
                        
                        // Strip machine name if it's already in the screen name
                        var machineNameLower = Global.MachineName.ToLowerInvariant();
                        var safeMachineNameLower = Global.SafeMachineName.ToLowerInvariant();
                        if (sanitizedName.StartsWith(machineNameLower)) sanitizedName = sanitizedName.Substring(machineNameLower.Length).TrimStart('_', '-', ' ');
                        else if (sanitizedName.StartsWith(safeMachineNameLower)) sanitizedName = sanitizedName.Substring(safeMachineNameLower.Length).TrimStart('_', '-', ' ');

                        // Strip generic "displayX" or "monitorX" if followed by more specific info (like "aocb419")
                        sanitizedName = System.Text.RegularExpressions.Regex.Replace(sanitizedName, @"^(display|monitor)\d+[_ \-]?(?=.)", "");

                        // Final cleanup
                        sanitizedName = sanitizedName.Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
                        if (string.IsNullOrEmpty(sanitizedName)) sanitizedName = $"display{screen.index + 1}";
                        else if (!sanitizedName.StartsWith("display")) sanitizedName = "display_" + sanitizedName;

                        string displayName = screenName;
                        if (displayName.StartsWith("\\\\.\\")) displayName = $"Display {screen.index + 1}";
                        
                        await CaptureAndPublish(deviceName, displayName, stoppingToken, sanitizedName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error in CameraService loop: {Message}", ex.Message);
            }

            // Refresh every 30 seconds (configurable later)
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task CaptureAndPublish(string display, string name, CancellationToken stoppingToken, string? idSuffix = null)
    {
        string suffix = idSuffix ?? "desktop";
        string topic = $"homeassistant/camera/{_uniqueId}_{suffix}/image";
        string discoveryTopic = $"homeassistant/camera/{_uniqueId}_{suffix}/config";

        string mjpegUrl = $"http://{Environment.MachineName.ToLowerInvariant()}:{_port}/api/screenshot/stream?display={Uri.EscapeDataString(display)}&token={_tokenService.Token}";

        // Register/Update Discovery
        var config = new
        {
            name = name,
            unique_id = $"{_uniqueId}_{suffix}",
            topic = topic,
            json_attributes_topic = $"{topic}/attributes",
            device = new
            {
                identifiers = new[] { _uniqueId },
                name = Global.MachineName,
                manufacturer = "MQTT.Agent",
                model = "Windows PC"
            }
        };

        var attributes = new
        {
            mjpeg_url = mjpegUrl,
            friendly_name = name,
            device_name = display
        };

        await _mqtt.EnqueueAsync(discoveryTopic, JsonSerializer.Serialize(config), true);
        await _mqtt.EnqueueAsync($"{topic}/attributes", JsonSerializer.Serialize(attributes), true);

        // Capture screenshot
        var bytes = await _screenshotService.CaptureScreenshot(quality: 50, display: display, format: "jpg");
        if (bytes != null)
        {
            // Publish binary image
            await _mqtt.EnqueueAsync(topic, bytes, false);
        }
    }
}
