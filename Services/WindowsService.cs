using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System;
using MqttAgent.Utils;

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

    private List<WindowInfo> _windows = new();
    private bool _wtsLockAvailable = true;

    public async Task<string> Lock()
    {
        try
        {
            var activeSessionId = NativeMethods.WTSGetActiveConsoleSessionId();
            Console.WriteLine($"Initiating lock for session {activeSessionId}...");
            
            if (_wtsLockAvailable)
            {
                try
                {
                    if (NativeMethods.WTSLockWorkStation(activeSessionId))
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

            if (NativeMethods.LockWorkStation())
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
                NativeMethods.WTSLogoffSession(IntPtr.Zero, 0xFFFFFFFF, false);
                return "Global logout initiated.";
            }
            else
            {
                if (force)
                {
                    // Use ExitWindowsEx with EWX_FORCE to forcefully close all apps
                    uint flags = NativeMethods.EWX_LOGOFF | NativeMethods.EWX_FORCE;
                    if (NativeMethods.ExitWindowsEx(flags, 0))
                    {
                        return "Forced logout initiated via ExitWindowsEx.";
                    }
                    // Fallback: try WTS if ExitWindowsEx fails
                }
                var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
                NativeMethods.WTSLogoffSession(IntPtr.Zero, sessionId, false);
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
            if (NativeMethods.InitiateSystemShutdownEx(null, message, (uint)timeout, force, reboot, 0))
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
        NativeMethods.EnumWindows(EnumWindowCallback, IntPtr.Zero);
        return _windows;
    }

    private bool EnumWindowCallback(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            var window = new WindowInfo { Handle = hWnd };
            var titleBuilder = new StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
            window.Title = titleBuilder.ToString();

            var classBuilder = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, classBuilder, classBuilder.Capacity);
            window.ClassName = classBuilder.ToString();

            uint processId;
            window.ThreadId = (int)NativeMethods.GetWindowThreadProcessId(hWnd, out processId);
            window.ProcessId = (int)processId;
            window.IsVisible = NativeMethods.IsWindowVisible(hWnd);
            window.IsEnabled = NativeMethods.IsWindowEnabled(hWnd);

            if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect))
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
