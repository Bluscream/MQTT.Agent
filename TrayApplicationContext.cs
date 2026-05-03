using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using MqttAgent.Services;

namespace MqttAgent;

public class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon _notifyIcon = null!;
    private readonly IServiceProvider _services;
    private readonly HttpClient _httpClient;
    private readonly bool _isClientOnly;
    private readonly string _baseUrl;
    private readonly string _token;

    public TrayApplicationContext(IServiceProvider services)
    {
        _services = services;
        _token = "7f46fda81f4d4b51878cdf01aca45804";
        _baseUrl = "http://localhost:23482";
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        
        // Determine if we are running alongside an existing service
        var port = 23482;
        try
        {
            var ipGlobalProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            var listeners = ipGlobalProperties.GetActiveTcpListeners();
            _isClientOnly = listeners.Any(l => l.Port == port);
        }
        catch { _isClientOnly = false; }

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Icon trayIcon;
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BlockShutdown.ico");
            if (File.Exists(iconPath))
            {
                trayIcon = new Icon(iconPath);
            }
            else
            {
                trayIcon = SystemIcons.Application;
            }
        }
        catch
        {
            trayIcon = SystemIcons.Application;
        }

        _notifyIcon = new NotifyIcon
        {
            Icon = trayIcon,
            Text = "MQTT.Agent",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();

        // Block Shutdown Toggle
        var blockShutdownItem = new ToolStripMenuItem("Block Shutdown", null, async (s, e) => await ToggleBlockShutdown((ToolStripMenuItem)s!));
        blockShutdownItem.CheckOnClick = false; // Manual handling
        
        contextMenu.Items.Add(blockShutdownItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        // Actions
        contextMenu.Items.Add(new ToolStripMenuItem("Lock Workstation", null, (s, e) => ExecuteAction("lock")));
        contextMenu.Items.Add(new ToolStripMenuItem("Reboot System", null, (s, e) => ExecuteAction("reboot")));
        contextMenu.Items.Add(new ToolStripMenuItem("Shutdown System", null, (s, e) => ExecuteAction("shutdown")));
        contextMenu.Items.Add(new ToolStripMenuItem("Logoff User", null, (s, e) => ExecuteAction("logoff")));
        
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, ExitApplication));

        contextMenu.Opening += async (s, e) => {
            if (_isClientOnly) {
                try {
                    var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/block-status");
                    if (resp.IsSuccessStatusCode) {
                        var json = await resp.Content.ReadAsStringAsync();
                        var data = System.Text.Json.JsonDocument.Parse(json);
                        blockShutdownItem.Checked = data.RootElement.GetProperty("enabled").GetBoolean();
                    }
                } catch { }
            } else {
                var blocker = _services.GetService<ShutdownBlockerService>();
                if (blocker != null) blockShutdownItem.Checked = blocker.IsBlockingEnabled;
            }
        };

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    private async Task ToggleBlockShutdown(ToolStripMenuItem item)
    {
        bool newState = !item.Checked;
        if (_isClientOnly)
        {
            try {
                await _httpClient.PostAsync($"{_baseUrl}/api/system/toggle-block?enabled={newState}", null);
                item.Checked = newState;
            } catch (Exception ex) {
                MessageBox.Show($"Failed to communicate with service: {ex.Message}");
            }
        }
        else
        {
            var mqtt = _services.GetService<IMqttManager>();
            if (mqtt != null)
            {
                var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
                var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
                var payload = newState ? "ON" : "OFF";
                await mqtt.EnqueueAsync(topic, payload, true);
                item.Checked = newState;
            }
        }
    }

    private async void ExecuteAction(string action)
    {
        if (_isClientOnly)
        {
            try {
                // For now we'll trigger actions via the service's existing ActionExecutor logic 
                // by publishing an internal MQTT message or just adding an API. 
                // Let's add a direct execution API to SystemController next.
                await _httpClient.PostAsync($"{_baseUrl}/api/system/execute?action={action}", null);
            } catch (Exception ex) {
                MessageBox.Show($"Failed to execute action via IPC: {ex.Message}");
            }
        }
        else
        {
            var winService = _services.GetService<WindowsService>();
            if (winService == null) return;
            switch(action) {
                case "lock": await winService.Lock(); break;
                case "reboot": winService.Shutdown(true, false, 0, "Restarting via Tray"); break;
                case "shutdown": winService.Shutdown(false, false, 0, "Shutting down via Tray"); break;
                case "logoff": winService.Logout(false, null, 0); break;
            }
        }
    }

    private void ExitApplication(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }
}
