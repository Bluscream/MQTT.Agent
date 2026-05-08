using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;

namespace MqttAgent.Services;

public class ShutdownBlockerService : IHostedService
{
    private readonly IMqttManager _mqttManager;
    private readonly ILogger<ShutdownBlockerService> _logger;
    private bool _blockShutdown;
    private string _machineName;
    private string _stateTopic;
    private string _commandTopic;

    private Thread? _messagePumpThread;
    private HiddenMessageForm? _hiddenForm;
    private readonly IServiceProvider _services;

    public ShutdownBlockerService(IMqttManager mqttManager, ILogger<ShutdownBlockerService> logger, IServiceProvider services)
    {
        _mqttManager = mqttManager;
        _logger = logger;
        _services = services;
        _machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        
        _stateTopic = $"homeassistant/switch/{_machineName}_block_shutdown/state";
        _commandTopic = $"homeassistant/switch/{_machineName}_block_shutdown/set";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttManager.SubscribeAsync(_commandTopic, HandleCommandAsync);
        await PublishStateAsync();

        // Start a hidden form on an STA thread to process Windows messages
        _messagePumpThread = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            _hiddenForm = new HiddenMessageForm(this);
            Application.Run(_hiddenForm);
        });
        _messagePumpThread.SetApartmentState(ApartmentState.STA);
        _messagePumpThread.IsBackground = true;
        _messagePumpThread.Start();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_hiddenForm != null && !_hiddenForm.IsDisposed)
        {
            _hiddenForm.Invoke(new Action(() => _hiddenForm.Close()));
        }
        return Task.CompletedTask;
    }

    private async Task HandleCommandAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        _blockShutdown = string.Equals(payload, "ON", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("Block Shutdown set to: {State}", _blockShutdown);
        await PublishStateAsync();
    }

    private async Task PublishStateAsync()
    {
        var state = _blockShutdown ? "ON" : "OFF";
        await _mqttManager.EnqueueAsync(_stateTopic, state, true);
    }

    public bool IsBlockingEnabled => _blockShutdown;

    private class HiddenMessageForm : Form
    {
        private readonly ShutdownBlockerService _service;
        private const int WM_QUERYENDSESSION = 0x11;
        private const int WM_ENDSESSION = 0x16;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, [MarshalAs(UnmanagedType.LPWStr)] string pwszReason);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool AbortSystemShutdown(string? lpMachineName);

        public HiddenMessageForm(ShutdownBlockerService service)
        {
            _service = service;
            this.Text = "MQTT.Agent Shutdown Blocker";
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.Opacity = 0;
            this.FormBorderStyle = FormBorderStyle.None;
        }

        protected override void SetVisibleCore(bool value)
        {
            // Never let the form become visible
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
            {
                var monitor = _service._services.GetService<SystemMonitorService>();
                if (monitor != null)
                {
                    _service._logger.LogInformation("Shutdown/Logoff detected via WM_QUERYENDSESSION");
                    // We don't know for sure if it's shutdown or logoff here, but we can assume shutdown if it's the whole system
                    // Actually, SystemEvents.SessionEnding is better for distinguishing, 
                    // but we can at least notify the monitor to check flags.
                }

                if (_service.IsBlockingEnabled)
                {
                    _service._logger.LogWarning("Blocked system shutdown/logoff attempt.");
                    ShutdownBlockReasonCreate(this.Handle, "MQTT.Agent has blocked shutdown via Home Assistant.");
                    m.Result = IntPtr.Zero; // 0 = block, 1 = allow
                    
                    // Abort any pending shutdowns aggressively via native API
                    try
                    {
                        if (!AbortSystemShutdown(null))
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error != 0 && error != 1116) // 1116 = ERROR_NO_SHUTDOWN_IN_PROGRESS
                            {
                                _service._logger.LogWarning("AbortSystemShutdown failed with error code: {Error}", error);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _service._logger.LogError(ex, "Error calling AbortSystemShutdown");
                    }
                    
                    return;
                }
                else
                {
                    ShutdownBlockReasonDestroy(this.Handle);
                }
            }
            base.WndProc(ref m);
        }
    }
}
