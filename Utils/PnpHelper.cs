using System;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace MqttAgent.Utils
{
    public static class PnpHelper
    {
        private const uint DICD_GENERATE_ID = 0x00000001;
        private const uint DIGCF_ALLCLASSES = 0x00000004;
        private const uint DIGCF_PRESENT = 0x00000002;

        private const uint DIF_PROPERTYCHANGE = 0x00000012;
        private const uint DICS_ENABLE = 0x00000001;
        private const uint DICS_DISABLE = 0x00000002;
        private const uint DICS_FLAG_GLOBAL = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid classGuid;
            public uint devInst;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_CLASSINSTALL_HEADER
        {
            public uint cbSize;
            public uint installFunction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_PROPCHANGE_PARAMS
        {
            public SP_CLASSINSTALL_HEADER classInstallHeader;
            public uint stateChange;
            public uint scope;
            public uint hwProfile;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, [MarshalAs(UnmanagedType.LPWStr)] string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, [MarshalAs(UnmanagedType.LPWStr)] string? enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, System.Text.StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, uint classInstallParamsSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        public static bool SetDeviceState(string instanceId, bool enable)
        {
            IntPtr infoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (infoSet == (IntPtr)(-1)) return false;

            try
            {
                SP_DEVINFO_DATA devData = new SP_DEVINFO_DATA();
                devData.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));

                uint i = 0;
                while (SetupDiEnumDeviceInfo(infoSet, i++, ref devData))
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                    if (SetupDiGetDeviceInstanceId(infoSet, ref devData, sb, (uint)sb.Capacity, out _))
                    {
                        if (sb.ToString().Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                        {
                            return ChangeState(infoSet, devData, enable);
                        }
                    }
                }
                return false;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }
        }

        private static bool ChangeState(IntPtr infoSet, SP_DEVINFO_DATA devData, bool enable)
        {
            SP_PROPCHANGE_PARAMS pcp = new SP_PROPCHANGE_PARAMS();
            pcp.classInstallHeader.cbSize = (uint)Marshal.SizeOf(typeof(SP_CLASSINSTALL_HEADER));
            pcp.classInstallHeader.installFunction = DIF_PROPERTYCHANGE;
            pcp.stateChange = enable ? DICS_ENABLE : DICS_DISABLE;
            pcp.scope = DICS_FLAG_GLOBAL;
            pcp.hwProfile = 0;

            if (SetupDiSetClassInstallParams(infoSet, ref devData, ref pcp, (uint)Marshal.SizeOf(typeof(SP_PROPCHANGE_PARAMS))))
            {
                return SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, infoSet, ref devData);
            }
            return false;
        }
    }
}
