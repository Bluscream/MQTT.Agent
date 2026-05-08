using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MqttAgent.Utils
{
    /// <summary>
    /// Manages Windows power schemes via native Win32 PowrProf.dll P/Invoke calls
    /// instead of shelling out to powercfg.exe.
    /// </summary>
    public static class PowerHelper
    {
        // ── P/Invoke declarations ──────────────────────────────────────────

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerEnumerate(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            uint AccessFlags,
            uint Index,
            ref Guid Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(
            IntPtr UserRootPowerKey,
            out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerSetActiveScheme(
            IntPtr UserRootPowerKey,
            ref Guid SchemeGuid);

        [DllImport("powrprof.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            StringBuilder Buffer,
            ref uint BufferSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private const uint ACCESS_SCHEME = 16;
        private const uint ERROR_SUCCESS = 0;
        private const uint ERROR_NO_MORE_ITEMS = 259;

        // ── Public API ─────────────────────────────────────────────────────

        /// <summary>
        /// Enumerates all power schemes using the native PowrProf API.
        /// </summary>
        public static List<(string Name, string Guid)> GetPowerSchemes()
        {
            var schemes = new List<(string Name, string Guid)>();
            try
            {
                uint index = 0;
                var schemeGuid = Guid.Empty;
                uint bufferSize = (uint)Marshal.SizeOf(typeof(Guid));

                while (true)
                {
                    schemeGuid = Guid.Empty;
                    bufferSize = (uint)Marshal.SizeOf(typeof(Guid));
                    uint result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, ref schemeGuid, ref bufferSize);

                    if (result == ERROR_NO_MORE_ITEMS) break;
                    if (result != ERROR_SUCCESS) break;

                    string name = GetSchemeFriendlyName(schemeGuid);
                    schemes.Add((name, schemeGuid.ToString()));
                    index++;
                }
            }
            catch { }
            return schemes;
        }

        /// <summary>
        /// Returns the friendly name of the currently active power scheme.
        /// </summary>
        public static string GetActiveScheme()
        {
            try
            {
                uint result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeGuidPtr);
                if (result != ERROR_SUCCESS) return "Unknown";

                try
                {
                    var activeGuid = Marshal.PtrToStructure<Guid>(activeGuidPtr);
                    return GetSchemeFriendlyName(activeGuid);
                }
                finally
                {
                    LocalFree(activeGuidPtr);
                }
            }
            catch { }
            return "Unknown";
        }

        /// <summary>
        /// Returns the GUID of the currently active power scheme.
        /// </summary>
        public static Guid? GetActiveSchemeGuid()
        {
            try
            {
                uint result = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activeGuidPtr);
                if (result != ERROR_SUCCESS) return null;

                try
                {
                    return Marshal.PtrToStructure<Guid>(activeGuidPtr);
                }
                finally
                {
                    LocalFree(activeGuidPtr);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Sets the active power scheme by friendly name using the native API.
        /// </summary>
        public static bool SetActiveScheme(string schemeName)
        {
            var schemes = GetPowerSchemes();
            var target = schemes.FirstOrDefault(s => s.Name.Equals(schemeName, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(target.Guid)) return false;

            try
            {
                var guid = Guid.Parse(target.Guid);
                uint result = PowerSetActiveScheme(IntPtr.Zero, ref guid);
                return result == ERROR_SUCCESS;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Sets the active power scheme by GUID using the native API.
        /// </summary>
        public static bool SetActiveScheme(Guid schemeGuid)
        {
            try
            {
                uint result = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
                return result == ERROR_SUCCESS;
            }
            catch
            {
                return false;
            }
        }

        // ── Private helpers ────────────────────────────────────────────────

        private static string GetSchemeFriendlyName(Guid schemeGuid)
        {
            try
            {
                uint nameSize = 0;
                // First call to get required buffer size
                PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, null!, ref nameSize);

                if (nameSize == 0) return schemeGuid.ToString();

                var nameBuffer = new StringBuilder((int)nameSize / 2);
                uint result = PowerReadFriendlyName(IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, nameBuffer, ref nameSize);

                if (result == ERROR_SUCCESS)
                {
                    return nameBuffer.ToString();
                }
            }
            catch { }
            return schemeGuid.ToString();
        }
    }
}
