using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MqttAgent.Utils;

namespace MqttAgent.Services;

public class ActionCenterPollerService : IHostedService
{
    private readonly SystemMonitorService _systemMonitorService;
    private readonly ILogger<ActionCenterPollerService> _logger;
    private readonly List<ActionCenterPoller> _pollers = new();

    public ActionCenterPollerService(SystemMonitorService systemMonitorService, ILogger<ActionCenterPollerService> logger)
    {
        _systemMonitorService = systemMonitorService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Global.IsTrayMode)
            {
                var port = 23482;
                try
                {
                    var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                    var listeners = ipGlobalProperties.GetActiveTcpListeners();
                    if (System.Linq.Enumerable.Any(listeners, l => l.Port == port))
                    {
                        _logger.LogInformation("Background service is already running. Tray app will defer Action Center polling.");
                        return Task.CompletedTask;
                    }
                }
                catch { }
            }

            var userDirs = System.IO.Directory.GetDirectories(@"C:\Users");
            foreach (var dir in userDirs)
            {
                var dbPath = System.IO.Path.Combine(dir, @"AppData\Local\Microsoft\Windows\Notifications\wpndatabase.db");
                if (System.IO.File.Exists(dbPath))
                {
                    var poller = new ActionCenterPoller(dbPath);
                    poller.OnNotification += HandleNotification;
                    _pollers.Add(poller);
                    _logger.LogInformation("ActionCenterPollerService tracking DB: {DbPath}", dbPath);
                }
            }
            if (_pollers.Count == 0)
            {
                _logger.LogWarning("No wpndatabase.db found for any users.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start ActionCenterPoller. Ensure Microsoft.Data.Sqlite is loaded and db path is accessible.");
        }
        
        return Task.CompletedTask;
    }

    private void HandleNotification(ActionCenterNotification notif)
    {
        try
        {
            if (notif.Payload == null) return;

            var appId = notif.AppId ?? "";
            var toastTitle = notif.Payload.ToastTitle ?? "";
            var toastBody = notif.Payload.ToastBody ?? "";
            var image1 = notif.Payload.Images.Count > 0 ? notif.Payload.Images[0] : "";
            var payload = notif.Payload.RawXml ?? "";
            
            _logger.LogInformation("Action Center Event: {AppId} - {Title}", appId, toastTitle);

            // Forward to HASS via SystemMonitorService
            _ = _systemMonitorService.ReportRichEvent($"Notification: {toastTitle}", "action_center_notification", new
            {
                app_id = appId,
                title = toastTitle,
                body = toastBody,
                image_url = image1,
                xml_payload = payload
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Action Center Notification");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var poller in _pollers)
        {
            poller.Dispose();
        }
        _pollers.Clear();
        return Task.CompletedTask;
    }
}
