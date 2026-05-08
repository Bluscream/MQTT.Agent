using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System;

namespace MqttAgent.Services;

public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int ProcessId { get; set; }
    public int ThreadId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class WindowsService
{
    private readonly ProcessService _processService;
    private readonly IServiceProvider _services;

    public WindowsService(ProcessService processService, IServiceProvider services)
    {
        _processService = processService;
        _services = services;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLockWorkStation(uint SessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint SessionId, bool bWait);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool InitiateSystemShutdownEx(string? lpMachineName, string? lpMessage, uint dwTimeout, bool bForceAppsClosed, bool bRebootAfterShutdown, uint dwReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    // ExitWindowsEx flags
    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_FORCE = 0x00000004;
    private const uint EWX_FORCEIFHUNG = 0x00000010;

    // Window Enumeration P/Invokes
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private List<WindowInfo> _windows = new();
    private bool _wtsLockAvailable = true;

    public async Task<string> Lock()
    {
        try
        {
            var activeSessionId = WTSGetActiveConsoleSessionId();
            Console.WriteLine($"Initiating lock for session {activeSessionId}...");
            
            if (_wtsLockAvailable)
            {
                try
                {
                    if (WTSLockWorkStation(activeSessionId))
                    {
                        return "Workstation locked successfully via WTS.";
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    _wtsLockAvailable = false;
                    Console.WriteLine("WARNING: WTSLockWorkStation not found. Falling back...");
                }
            }

            if (LockWorkStation())
            {
                return "Workstation locked successfully via user32.dll.";
            }

            try
            {
                await _processService.StartProcess("rundll32.exe", "user32.dll,LockWorkStation", asUser: activeSessionId.ToString());
                return "Workstation lock command sent via session-aware fallback (rundll32).";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Final fallback failed: {ex.Message}");
            }

            throw new Exception("Failed to lock workstation after trying multiple methods.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Lock failed: {ex.Message}");
        }
    }

    public string Logout(bool allUsers = false, string? message = null, int timeout = 0, bool force = false)
    {
        var blocker = (ShutdownBlockerService?)_services.GetService(typeof(ShutdownBlockerService));
        if (blocker != null && blocker.IsBlockingEnabled)
        {
            throw new Exception("Logout blocked: 'Block Shutdown' is currently enabled in MQTT Agent.");
        }

        try
        {
            if (allUsers)
            {
                // Logoff all sessions via WTS
                WTSLogoffSession(IntPtr.Zero, 0xFFFFFFFF, false);
                return "Global logout initiated.";
            }
            else
            {
                if (force)
                {
                    // Use ExitWindowsEx with EWX_FORCE to forcefully close all apps
                    uint flags = EWX_LOGOFF | EWX_FORCE;
                    if (ExitWindowsEx(flags, 0))
                    {
                        return "Forced logout initiated via ExitWindowsEx.";
                    }
                    // Fallback: try WTS if ExitWindowsEx fails
                }
                var sessionId = WTSGetActiveConsoleSessionId();
                WTSLogoffSession(IntPtr.Zero, sessionId, false);
                return $"Logout initiated for session {sessionId}.";
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Logout failed: {ex.Message}");
        }
    }

    public string Shutdown(bool reboot = false, bool force = true, int timeout = 0, string? message = null)
    {
        var blocker = (ShutdownBlockerService?)_services.GetService(typeof(ShutdownBlockerService));
        if (blocker != null && blocker.IsBlockingEnabled)
        {
            string action = reboot ? "Reboot" : "Shutdown";
            throw new Exception($"{action} blocked: 'Block Shutdown' is currently enabled in MQTT Agent.");
        }

        try
        {
            if (InitiateSystemShutdownEx(null, message, (uint)timeout, force, reboot, 0))
            {
                return (reboot ? "Reboot" : "Shutdown") + " initiated.";
            }
            throw new Exception("InitiateSystemShutdownEx returned false.");
        }
        catch (Exception ex)
        {
            throw new Exception($"Shutdown/Reboot operation failed: {ex.Message}");
        }
    }

    // Window Enumeration Logic
    public List<WindowInfo> ListWindows()
    {
        _windows.Clear();
        EnumWindows(EnumWindowCallback, IntPtr.Zero);
        return _windows;
    }

    private bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var window = new WindowInfo { Handle = hWnd };
            var titleBuilder = new StringBuilder(256);
            GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            window.Title = titleBuilder.ToString();

            var classBuilder = new StringBuilder(256);
            GetClassName(hWnd, classBuilder, classBuilder.Capacity);
            window.ClassName = classBuilder.ToString();

            uint processId;
            window.ThreadId = (int)GetWindowThreadProcessId(hWnd, out processId);
            window.ProcessId = (int)processId;
            window.IsVisible = IsWindowVisible(hWnd);
            window.IsEnabled = IsWindowEnabled(hWnd);

            if (GetWindowRect(hWnd, out RECT rect))
            {
                window.X = rect.Left;
                window.Y = rect.Top;
                window.Width = rect.Right - rect.Left;
                window.Height = rect.Bottom - rect.Top;
            }
            _windows.Add(window);
        }
        catch { }
        return true;
    }
}
