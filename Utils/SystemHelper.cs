using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Management;
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
            // Simplified check: if console session is disconnected, it's usually locked
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
            catch
            {
                // Ignore exceptions
            }

            return users;
        }



        public static HashSet<IntPtr> FlashingWindows { get; } = new HashSet<IntPtr>();
        
        public static MqttAgent.Models.NeedsAttentionInfo? GetNeedsAttentionInfo()
        {
            MqttAgent.Models.NeedsAttentionInfo? info = null;
            if (FlashingWindows.Count > 0)
            {
                // Verify the flashing windows still exist
                var toRemove = new List<IntPtr>();
                foreach (var fw in FlashingWindows)
                {
                    if (!fw.IsVisible())
                    {
                        toRemove.Add(fw);
                    }
                    else if (info == null)
                    {
                        info = GetWindowInfo(fw);
                    }
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
                        if (className == "#32770") // Dialog box
                        {
                            info = GetWindowInfo(hWnd);
                            info.ClassName = className;
                            return false; // Stop enumerating
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
                
                if (process.TryGetCommandLine(out var cmdLine))
                {
                    info.CommandLine = cmdLine;
                }
            }
            else
            {
                info.ProcessName = "Unknown";
            }

            return info;
        }

        public static bool IsNeedsAttention() => GetNeedsAttentionInfo() != null;
    }
}
