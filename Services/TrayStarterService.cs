using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MqttAgent.Utils;

namespace MqttAgent.Services;

public class TrayStarterService : BackgroundService
{
    private readonly ProcessService _processService;
    private readonly ILogger<TrayStarterService> _logger;

    public TrayStarterService(ProcessService processService, ILogger<TrayStarterService> logger)
    {
        _processService = processService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Global.IsStartTrayEnabled || !Global.IsServiceMode) return;

        _logger.LogInformation("TrayStarterService: Checking for active user sessions to start tray...");

        // Small delay to ensure WTS is ready if we started at boot
        await Task.Delay(2000, stoppingToken);

        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId != 0 && sessionId != 0xFFFFFFFF)
        {
            _logger.LogInformation("Active user session detected (ID: {SessionId}). (Re)starting tray app...", sessionId);
            
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe != null)
            {
                var trayName = Path.GetFileNameWithoutExtension(currentExe);
                var currentPid = Process.GetCurrentProcess().Id;

                foreach (var p in Process.GetProcessesByName(trayName))
                {
                    try
                    {
                        if (p.Id != currentPid)
                        {
                            _logger.LogInformation("Killing existing tray process: {Pid}", p.Id);
                            p.Kill();
                            await p.WaitForExitAsync(stoppingToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to kill existing tray process {Pid}: {Message}", p.Id, ex.Message);
                    }
                }

                // Start new tray app
                var token = Config.Get("token", "-token");
                var args = $"--tray -token {token}";
                if (Global.IsMoreStatesEnabled) args += " --more-states";
                
                try
                {
                    await _processService.StartProcess(currentExe, args, asUser: sessionId.ToString());
                    _logger.LogInformation("Tray app started in session {SessionId}.", sessionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start tray app in session {SessionId}", sessionId);
                }
            }
        }
        else
        {
            _logger.LogInformation("No active user session detected. TrayStarterService idle.");
        }
    }
}
