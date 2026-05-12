using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Management;
using System.Diagnostics;
using System.Linq;
using MqttAgent.Models;

namespace MqttAgent.Utils
{
    public static class SystemHelper
    {
        private const int SM_CLEANBOOT = 67;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int smIndex);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern IntPtr WTSOpenServer([MarshalAs(UnmanagedType.LPStr)] String pServerName);

        [DllImport("wtsapi32.dll")]
        static extern void WTSCloseServer(IntPtr hServer);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSEnumerateSessions(
            IntPtr hServer,
            int Reserved,
            int Version,
            ref IntPtr ppSessionInfo,
            ref int pCount);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        [DllImport("Wtsapi32.dll")]
        static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out System.IntPtr ppBuffer, out uint pBytesReturned);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public Int32 SessionID;
            [MarshalAs(UnmanagedType.LPStr)]
            public String pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        private enum WTS_INFO_CLASS
        {
            WTSInitialProgram,
            WTSApplicationName,
            WTSWorkingDirectory,
            WTSOEMId,
            WTSSessionId,
            WTSUserName,
            WTSWinStationName,
            WTSDomainName,
            WTSConnectState,
            WTSClientBuildNumber,
            WTSClientName,
            WTSClientDirectory,
            WTSClientProductId,
            WTSClientHardwareId,
            WTSClientAddress,
            WTSClientDisplay,
            WTSClientProtocolType
        }

        private enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit
        }

        public static bool IsSafeMode()
        {
            return GetSystemMetrics(SM_CLEANBOOT) != 0;
        }

        public static bool IsUserLoggedIn()
        {
            return GetLoggedInUsers().Count > 0;
        }

        public static bool IsLocked()
        {
            IntPtr serverHandle = IntPtr.Zero;
            IntPtr sessionInfoPtr = IntPtr.Zero;
            int sessionCount = 0;

            if (WTSEnumerateSessions(serverHandle, 0, 1, ref sessionInfoPtr, ref sessionCount))
            {
                int dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                IntPtr currentSession = sessionInfoPtr;

                for (int i = 0; i < sessionCount; i++)
                {
                    WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(currentSession, typeof(WTS_SESSION_INFO))!;
                    if (si.SessionID == 1 || si.pWinStationName.Contains("Console", StringComparison.OrdinalIgnoreCase))
                    {
                        if (si.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                        {
                            WTSFreeMemory(sessionInfoPtr);
                            return true;
                        }
                    }
                    currentSession += dataSize;
                }
                WTSFreeMemory(sessionInfoPtr);
            }
            return false;
        }

        public static List<string> GetLoggedInUsers()
        {
            List<string> users = new List<string>();
            IntPtr serverHandle = IntPtr.Zero;
            IntPtr sessionInfoPtr = IntPtr.Zero;
            int sessionCount = 0;

            try
            {
                if (WTSEnumerateSessions(serverHandle, 0, 1, ref sessionInfoPtr, ref sessionCount))
                {
                    int dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                    IntPtr currentSession = sessionInfoPtr;

                    for (int i = 0; i < sessionCount; i++)
                    {
                        WTS_SESSION_INFO si = (WTS_SESSION_INFO)Marshal.PtrToStructure(currentSession, typeof(WTS_SESSION_INFO))!;
                        currentSession += dataSize;

                        if (si.State == WTS_CONNECTSTATE_CLASS.WTSActive || si.State == WTS_CONNECTSTATE_CLASS.WTSDisconnected)
                        {
                            IntPtr buffer = IntPtr.Zero;
                            uint bytesReturned = 0;
                            if (WTSQuerySessionInformation(serverHandle, si.SessionID, WTS_INFO_CLASS.WTSUserName, out buffer, out bytesReturned))
                            {
                                string? userName = Marshal.PtrToStringAnsi(buffer);
                                WTSFreeMemory(buffer);

                                if (!string.IsNullOrEmpty(userName) && userName != "SYSTEM" && userName != "LOCAL SERVICE" && userName != "NETWORK SERVICE")
                                {
                                    users.Add(userName);
                                }
                            }
                        }
                    }
                    WTSFreeMemory(sessionInfoPtr);
                }
            }
            catch { }
            return users;
        }

        public static HashSet<IntPtr> FlashingWindows { get; } = new HashSet<IntPtr>();
        
        public static MqttAgent.Models.NeedsAttentionInfo? GetNeedsAttentionInfo()
        {
            // 0. Check for Secure Desktop (UAC / Login Screen)
            string desktop = GetActiveDesktopName();
            if (desktop == "Winlogon")
            {
                return new NeedsAttentionInfo
                {
                    ProcessName = "csrss",
                    WindowName = "Secure Desktop (UAC/Login)",
                    ClassName = "Winlogon",
                    CommandLine = "Input Desktop: Winlogon"
                };
            }

            // 1. Check for known security/UAC processes first
            var securityProcesses = new[] { "consent", "CredentialUIBroker" };
            foreach (var pName in securityProcesses)
            {
                try
                {
                    var processes = Process.GetProcessesByName(pName);
                    if (processes.Length > 0)
                    {
                        var p = processes[0];
                        return new NeedsAttentionInfo
                        {
                            ProcessName = p.ProcessName,
                            ProcessId = p.Id,
                            WindowName = pName == "consent" ? "User Account Control" : "Windows Security",
                            ClassName = "SecurityPrompt",
                            CommandLine = p.TryGetCommandLine(out var cmd) ? cmd : null
                        };
                    }
                }
                catch { }
            }

            MqttAgent.Models.NeedsAttentionInfo? info = null;
            if (FlashingWindows.Count > 0)
            {
                var toRemove = new List<IntPtr>();
                foreach (var fw in FlashingWindows)
                {
                    if (!fw.IsVisible()) toRemove.Add(fw);
                    else if (info == null) info = GetWindowInfo(fw);
                }
                foreach (var r in toRemove) FlashingWindows.Remove(r);
            }

            if (info != null) return info;

            try
            {
                NativeMethods.EnumWindows((hWnd, lParam) =>
                {
                    if (hWnd.IsVisible())
                    {
                        string className = hWnd.GetWindowClassName();
                        if (className == "#32770" || className == "Credential Dialog Xaml Host")
                        {
                            info = GetWindowInfo(hWnd);
                            info.ClassName = className;
                            return false;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return info;
        }

        public static MqttAgent.Models.NeedsAttentionInfo GetWindowInfo(IntPtr hWnd)
        {
            var info = new MqttAgent.Models.NeedsAttentionInfo();
            info.WindowName = hWnd.GetWindowTitle();
            info.ClassName = hWnd.GetWindowClassName();

            if (hWnd.TryGetProcess(out var process) && process != null)
            {
                info.ProcessName = process.ProcessName;
                info.ProcessId = process.Id;
                if (process.TryGetCommandLine(out var cmdLine)) info.CommandLine = cmdLine;
            }
            else
            {
                info.ProcessName = "Unknown";
            }
            return info;
        }

        public static string GetActiveDesktopName()
        {
            IntPtr hDesktop = NativeMethods.OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
            if (hDesktop == IntPtr.Zero) return "Unknown";

            try
            {
                uint needed;
                NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, null!, 0, out needed);
                if (needed > 0)
                {
                    byte[] buffer = new byte[needed];
                    if (NativeMethods.GetUserObjectInformation(hDesktop, NativeMethods.UOI_NAME, buffer, needed, out _))
                    {
                        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                    }
                }
            }
            finally
            {
                NativeMethods.CloseDesktop(hDesktop);
            }
            return "Unknown";
        }

        public static bool IsNeedsAttention() => GetNeedsAttentionInfo() != null;
    }
}
