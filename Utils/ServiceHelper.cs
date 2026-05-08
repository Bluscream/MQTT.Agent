using System;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace MqttAgent.Utils
{
    public static class ServiceHelper
    {
        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        private const uint SERVICE_AUTO_START = 0x00000002;
        private const uint SERVICE_ERROR_NORMAL = 0x00000001;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint SERVICE_STOPPED = 0x00000001;
        private const uint SERVICE_RUNNING = 0x00000004;
        private const uint SERVICE_QUERY_STATUS = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string? lpDisplayName,
            uint dwDesiredAccess,
            uint dwServiceType,
            uint dwStartType,
            uint dwErrorControl,
            string lpBinaryPathName,
            string? lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string? lpDependencies,
            string? lpServiceStartName,
            string? lpPassword);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, string[]? lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

        public static bool IsServiceInstalled(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) return false;

            IntPtr svc = OpenService(scm, serviceName, SERVICE_ALL_ACCESS);
            bool installed = svc != IntPtr.Zero;

            if (installed) CloseServiceHandle(svc);
            CloseServiceHandle(scm);

            return installed;
        }

        public static void InstallService(string serviceName, string displayName, string binaryPath)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                IntPtr svc = CreateService(
                    scm,
                    serviceName,
                    displayName,
                    SERVICE_ALL_ACCESS,
                    SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_AUTO_START,
                    SERVICE_ERROR_NORMAL,
                    binaryPath,
                    null, IntPtr.Zero, null, null, null);

                if (svc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1073) // Already exists
                        throw new Win32Exception(err);
                }
                else
                {
                    CloseServiceHandle(svc);
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        public static void StartService(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                IntPtr svc = OpenService(scm, serviceName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

                if (!StartService(svc, 0, null))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1056) // Already running
                        throw new Win32Exception(err);
                }
                CloseServiceHandle(svc);
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        public static bool IsServiceRunning(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) return false;

            try
            {
                IntPtr svc = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return false;

                SERVICE_STATUS status = new SERVICE_STATUS();
                if (QueryServiceStatus(svc, ref status))
                {
                    bool running = status.dwCurrentState == SERVICE_RUNNING;
                    CloseServiceHandle(svc);
                    return running;
                }
                CloseServiceHandle(svc);
            }
            finally
            {
                CloseServiceHandle(scm);
            }
            return false;
        }

        public static void StopService(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                IntPtr svc = OpenService(scm, serviceName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

                SERVICE_STATUS status = new SERVICE_STATUS();
                if (!ControlService(svc, SERVICE_CONTROL_STOP, ref status))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1062) // Not running
                        throw new Win32Exception(err);
                }
                CloseServiceHandle(svc);
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }

        public static void UninstallService(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                IntPtr svc = OpenService(scm, serviceName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) return;

                if (!DeleteService(svc))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                CloseServiceHandle(svc);
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
    }
}
