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
    private readonly ILogger<CameraService> _logger;
    private readonly string _uniqueId;
    private readonly string _topic;

    public CameraService(IMqttManager mqtt, ScreenshotService screenshotService, ILogger<CameraService> logger)
    {
        _mqtt = mqtt;
        _screenshotService = screenshotService;
        _logger = logger;
        _uniqueId = Global.UniqueId;
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
                        string sanitizedName = screenName.Replace("\\\\.\\", "").Replace(" ", "_").Replace("-", "_").ToLowerInvariant();
                        string displayName = $"Display {screen.index + 1} ({screenName})";
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

        // Register/Update Discovery
        var config = new
        {
            name = $"{Global.MachineName} {name}",
            unique_id = $"{_uniqueId}_{suffix}",
            topic = topic,
            device = new
            {
                identifiers = new[] { _uniqueId },
                name = Global.MachineName,
                manufacturer = "MQTT.Agent",
                model = "Windows PC"
            }
        };

        await _mqtt.EnqueueAsync(discoveryTopic, JsonSerializer.Serialize(config), true);

        // Capture screenshot
        var result = await _screenshotService.CaptureScreenshot(quality: 50, display: display, format: "jpg");
        if (result != null && result.StartsWith("data:image/jpeg;base64,"))
        {
            var base64 = result.Substring("data:image/jpeg;base64,".Length);
            var bytes = Convert.FromBase64String(base64);
            
            // Publish binary image
            await _mqtt.EnqueueAsync(topic, bytes, false);
        }
    }
}
