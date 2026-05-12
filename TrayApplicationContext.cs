using System;
using MqttAgent.Utils;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using MqttAgent.Services;
using System.Security.Principal;

namespace MqttAgent;

public class TrayApplicationContext : ApplicationContext
{
    private NotifyIcon _notifyIcon = null!;
    private readonly IServiceProvider _services;
    private readonly HttpClient _httpClient;
    private readonly bool _isClientOnly;
    private readonly string _baseUrl;
    private readonly string _token;

    private HiddenMessageWindow _messageWindow;

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

        _messageWindow = new HiddenMessageWindow();
        _messageWindow.Show();

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
        
        // Force Action Toggle
        var forceActionItem = new ToolStripMenuItem("Force Action", null, async (s, e) => await ToggleForceAction((ToolStripMenuItem)s!));
        forceActionItem.CheckOnClick = false; // Manual handling

        // Service Running Toggle
        var serviceToggleItem = new ToolStripMenuItem("Service Running", null, (s, e) => ToggleService((ToolStripMenuItem)s!));
        serviceToggleItem.CheckOnClick = false; // Manual handling
        serviceToggleItem.ToolTipText = "Start or Stop the background MQTT.Agent service (Requires Admin)";

        // Persistence Setup
        var setupPersistenceItem = new ToolStripMenuItem("Setup Persistence", null, async (s, e) => {
            var persistence = _services.GetRequiredService<IPersistenceService>();
            persistence.EnsureServiceSafeBoot(); // Installs service + Safe Mode
            persistence.EnsureMoreStatesTriggers(); // Setup Task Scheduler, Run keys, etc.
            MessageBox.Show("Persistence setup complete! The service has been installed and logon triggers have been created.", "MQTT.Agent", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });

        contextMenu.Items.Add(blockShutdownItem);
        contextMenu.Items.Add(forceActionItem);
        contextMenu.Items.Add(serviceToggleItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(setupPersistenceItem);
        contextMenu.Items.Add(new ToolStripSeparator());

        // Actions
        contextMenu.Items.Add(new ToolStripMenuItem("Lock Workstation", null, (s, e) => ExecuteAction("lock")));
        contextMenu.Items.Add(new ToolStripMenuItem("Reboot System", null, (s, e) => ExecuteAction("reboot")));
        contextMenu.Items.Add(new ToolStripMenuItem("Shutdown System", null, (s, e) => ExecuteAction("shutdown")));
        contextMenu.Items.Add(new ToolStripMenuItem("Logoff User", null, (s, e) => ExecuteAction("logoff")));
        
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, ExitApplication));

        contextMenu.Opening += async (s, e) => {
            // Update Service Status (Admin required for some actions, but status is readable)
            serviceToggleItem.Checked = ServiceHelper.IsServiceRunning("MqttAgent");
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
            serviceToggleItem.Enabled = isAdmin;

            if (_isClientOnly) {
                try {
                    var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/block-status");
                    if (resp.IsSuccessStatusCode) {
                        var json = await resp.Content.ReadAsStringAsync();
                        var data = System.Text.Json.JsonDocument.Parse(json);
                        blockShutdownItem.Checked = data.RootElement.GetProperty("enabled").GetBoolean();
                    }
                } catch { }
                try {
                    var resp = await _httpClient.GetAsync($"{_baseUrl}/api/system/force-status");
                    if (resp.IsSuccessStatusCode) {
                        var json = await resp.Content.ReadAsStringAsync();
                        var data = System.Text.Json.JsonDocument.Parse(json);
                        forceActionItem.Checked = data.RootElement.GetProperty("enabled").GetBoolean();
                    }
                } catch { }
            } else {
                var blocker = _services.GetService<ShutdownBlockerService>();
                if (blocker != null) blockShutdownItem.Checked = blocker.IsBlockingEnabled;
                var forcer = _services.GetService<ForceActionService>();
                if (forcer != null) forceActionItem.Checked = forcer.IsForceEnabled;
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
                var machineName = Global.SafeMachineName;
                var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
                var payload = newState ? "ON" : "OFF";
                await mqtt.EnqueueAsync(topic, payload, true);
                item.Checked = newState;
            }
        }
    }

    private void ToggleService(ToolStripMenuItem item)
    {
        try {
            bool isRunning = ServiceHelper.IsServiceRunning("MqttAgent");
            if (isRunning) {
                ServiceHelper.StopService("MqttAgent");
                item.Checked = false;
            } else {
                ServiceHelper.StartService("MqttAgent");
                item.Checked = true;
            }
        } catch (Exception ex) {
            MessageBox.Show($"Failed to toggle service: {ex.Message}\n\nMake sure the application is running as Administrator.", "MQTT.Agent", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ToggleForceAction(ToolStripMenuItem item)
    {
        bool newState = !item.Checked;
        if (_isClientOnly)
        {
            try {
                await _httpClient.PostAsync($"{_baseUrl}/api/system/toggle-force?enabled={newState}", null);
                item.Checked = newState;
            } catch (Exception ex) {
                MessageBox.Show($"Failed to communicate with service: {ex.Message}");
            }
        }
        else
        {
            var forcer = _services.GetService<ForceActionService>();
            if (forcer != null)
            {
                await forcer.SetForceEnabled(newState);
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
            var forcer = _services.GetService<ForceActionService>();
            bool force = forcer?.IsForceEnabled ?? false;
            if (winService == null) return;
            switch(action) {
                case "lock": await winService.Lock(); break;
                case "reboot": winService.Shutdown(true, force, 0, "Restarting via Tray"); break;
                case "shutdown": winService.Shutdown(false, force, 0, "Shutting down via Tray"); break;
                case "logoff": winService.Logout(false, null, 0, force); break;
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

public class HiddenMessageWindow : Form
{
    private readonly int _msgShellHook;
    private const int HSHELL_FLASH = 0x8006;
    private const int HSHELL_WINDOWACTIVATED = 4;
    private const int HSHELL_RUDEAPPACTIVATED = 32772;

    public HiddenMessageWindow()
    {
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.Load += (s, e) =>
        {
            this.Size = new Size(0, 0);
            NativeMethods.RegisterShellHookWindow(this.Handle);
        };
        _msgShellHook = NativeMethods.RegisterWindowMessage("SHELLHOOK");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _msgShellHook)
        {
            int wParam = m.WParam.ToInt32();
            if (wParam == HSHELL_FLASH)
            {
                SystemHelper.FlashingWindows.Add(m.LParam);
            }
            else if (wParam == HSHELL_WINDOWACTIVATED || wParam == HSHELL_RUDEAPPACTIVATED)
            {
                SystemHelper.FlashingWindows.Remove(m.LParam);
            }
        }
        base.WndProc(ref m);
    }
}
