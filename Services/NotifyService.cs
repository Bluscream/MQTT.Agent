using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MqttAgent.Services;

public class NotifyService
{
    private readonly ProcessService _processService;
    private static readonly Uri OVRT_WEBSOCKET_URI = new("ws://127.0.0.1:11450/api");
    private static ClientWebSocket? _ovrtWebsocket;
    private static readonly SemaphoreSlim _ovrtLock = new(1, 1);
    private const int XSO_PORT = 42069;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    public NotifyService(ProcessService processService)
    {
        _processService = processService;
    }

    public async Task<string> NotifyAsync(
        string title,
        string message,
        bool toast = false,
        bool messagebox = false,
        bool ovrtoolkit = false,
        bool xsoverlay = false,
        string type = "MB_OK",
        string icon = "MB_ICONINFORMATION",
        int timeoutMs = 5000)
    {
        var results = new List<string>();

        if (toast)
        {
            try { await SendToastAsync(title, message); results.Add("Toast sent"); }
            catch (Exception ex) { results.Add($"Toast failed: {ex.Message}"); }
        }

        if (messagebox)
        {
            try { await ShowMessageBoxAsync(title, message, type, icon, timeoutMs); results.Add("MessageBox shown"); }
            catch (Exception ex) { results.Add($"MessageBox failed: {ex.Message}"); }
        }

        if (ovrtoolkit)
        {
            try { if (await SendOvrToolkitAsync(title, message)) results.Add("OVRToolkit sent"); else results.Add("OVRToolkit failed (connect)"); }
            catch (Exception ex) { results.Add($"OVRToolkit failed: {ex.Message}"); }
        }

        if (xsoverlay)
        {
            try { if (SendXSOverlay(title, message, timeoutMs)) results.Add("XSOverlay sent"); else results.Add("XSOverlay failed"); }
            catch (Exception ex) { results.Add($"XSOverlay failed: {ex.Message}"); }
        }

        return string.Join(", ", results);
    }

    private async Task SendToastAsync(string title, string message)
    {
        var escapedTitle = title.Replace("\"", "`\"").Replace("'", "''");
        var escapedMessage = message.Replace("\"", "`\"").Replace("'", "''");

        // PowerShell script to send toast notification
        var script = $@"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$APP_ID = 'IpcMcp Notifications'
$template = @'
<toast>
    <visual>
        <binding template=''ToastGeneric''>
            <text>{escapedTitle}</text>
            <text>{escapedMessage}</text>
        </binding>
    </visual>
</toast>
'@

$xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
$xml.LoadXml($template)
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($APP_ID).Show($toast)
";

        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new Exception("No active console session found");

        await _processService.StartProcess("powershell.exe", $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script}\"", asUser: sessionId.ToString());
    }

    private async Task ShowMessageBoxAsync(string title, string message, string type, string icon, int timeoutMs)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) throw new Exception("No active console session found");

        var args = $"--title \"{title}\" --message \"{message}\" --type \"{type}\" --icon \"{icon}\" --timeout {timeoutMs}";
        
        try
        {
            await _processService.StartProcess("msgbox.exe", args, asUser: sessionId.ToString());
        }
        catch (Exception ex)
        {
            // Final fallback: Use WTSSendMessage directly if msgbox.exe is not in PATH or fails
            ShowWtsMessageBox(sessionId, title, message, type, icon, timeoutMs);
            throw new Exception($"msgbox.exe failed: {ex.Message}. Fallback to WTSSendMessage used.");
        }
    }

    private void ShowWtsMessageBox(uint sessionId, string title, string message, string type, string icon, int timeoutMs)
    {
        uint style = 0; // MB_OK
        if (type.Contains("OKCANCEL", StringComparison.OrdinalIgnoreCase)) style = 1;
        else if (type.Contains("ABORTRETRYIGNORE", StringComparison.OrdinalIgnoreCase)) style = 2;
        else if (type.Contains("YESNOCANCEL", StringComparison.OrdinalIgnoreCase)) style = 3;
        else if (type.Contains("YESNO", StringComparison.OrdinalIgnoreCase)) style = 4;
        else if (type.Contains("RETRYCANCEL", StringComparison.OrdinalIgnoreCase)) style = 5;

        if (icon.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || icon.Contains("HAND", StringComparison.OrdinalIgnoreCase) || icon.Contains("STOP", StringComparison.OrdinalIgnoreCase)) style |= 0x10;
        else if (icon.Contains("QUESTION", StringComparison.OrdinalIgnoreCase)) style |= 0x20;
        else if (icon.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || icon.Contains("EXCLAMATION", StringComparison.OrdinalIgnoreCase)) style |= 0x30;
        else if (icon.Contains("INFORMATION", StringComparison.OrdinalIgnoreCase) || icon.Contains("ASTERISK", StringComparison.OrdinalIgnoreCase)) style |= 0x40;

        WTSSendMessage(IntPtr.Zero, sessionId, title, (uint)title.Length * 2, message, (uint)message.Length * 2, style, (uint)timeoutMs / 1000, out _, false);
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessage(
        IntPtr hServer,
        uint SessionId,
        string pTitle,
        uint TitleLength,
        string pMessage,
        uint MessageLength,
        uint Style,
        uint Timeout,
        out uint pResponse,
        bool bWait);

    private async Task<bool> SendOvrToolkitAsync(string title, string message)
    {
        await _ovrtLock.WaitAsync();
        try
        {
            if (_ovrtWebsocket?.State != WebSocketState.Open)
            {
                _ovrtWebsocket?.Dispose();
                _ovrtWebsocket = new ClientWebSocket();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _ovrtWebsocket.ConnectAsync(OVRT_WEBSOCKET_URI, cts.Token);
            }

            var hudMsg = new
            {
                messageType = "SendNotification",
                json = JsonSerializer.Serialize(new
                {
                    title = title,
                    body = message,
                    icon = "" // Placeholder for now
                })
            };

            var json = JsonSerializer.Serialize(hudMsg);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ovrtWebsocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _ovrtLock.Release();
        }
    }

    private bool SendXSOverlay(string title, string message, int timeoutMs)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var endPoint = new IPEndPoint(IPAddress.Loopback, XSO_PORT);

            var msg = new
            {
                messageType = 1,
                title = title,
                content = message,
                height = message.Length > 200 ? 200f : 150f,
                sourceApp = "IpcMcp",
                timeout = timeoutMs / 1000f,
                audioPath = "",
                useBase64Icon = false,
                icon = "",
                opacity = 1.0f
            };

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(msg);
            socket.SendTo(jsonBytes, endPoint);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
