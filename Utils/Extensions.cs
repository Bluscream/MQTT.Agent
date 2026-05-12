using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using MqttAgent.Models;

namespace MqttAgent.Utils;

public static class Extensions
{
    #region Hardware Extensions
    public static float GetSensorValue(this IHardware hardware, SensorType type, string name, float defaultValue = 0)
    {
        return hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value ?? defaultValue;
    }

    public static ISensor? GetSensor(this IHardware hardware, SensorType type, string name)
    {
        return hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name);
    }

    public static float GetValue(this IEnumerable<ISensor> sensors, string name, float defaultValue = 0)
    {
        return sensors.FirstOrDefault(s => s.Name == name)?.Value ?? defaultValue;
    }

    public static float GetTotalValue(this IEnumerable<ISensor> sensors, SensorType type)
    {
        return sensors.Where(s => s.SensorType == type).Sum(s => s.Value ?? 0);
    }

    public static float GetMaxSensorValue(this IEnumerable<IHardware> hardwareList, HardwareType type, SensorType sensorType, string sensorName, float defaultValue = 0)
    {
        var sensors = hardwareList
            .Where(h => h.HardwareType == type)
            .SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == sensorType && s.Name == sensorName);
        
        return sensors.Any() ? sensors.Max(s => s.Value ?? defaultValue) : defaultValue;
    }

    public static float GetMaxGpuSensorValue(this IEnumerable<IHardware> hardwareList, SensorType sensorType, string sensorName, float defaultValue = 0)
    {
        var gpuTypes = new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel };
        var sensors = hardwareList
            .Where(h => gpuTypes.Contains(h.HardwareType))
            .SelectMany(h => h.Sensors)
            .Where(s => s.SensorType == sensorType && s.Name == sensorName);

        return sensors.Any() ? sensors.Max(s => s.Value ?? defaultValue) : defaultValue;
    }
    #endregion

    #region WMI Extensions
    public static string? GetPropertyString(this ManagementBaseObject obj, string propertyName, string? defaultValue = null)
    {
        try { return obj[propertyName]?.ToString() ?? defaultValue; } catch { return defaultValue; }
    }

    public static long GetPropertyLong(this ManagementBaseObject obj, string propertyName, long defaultValue = 0)
    {
        try { return Convert.ToInt64(obj[propertyName] ?? defaultValue); } catch { return defaultValue; }
    }
    #endregion

    #region Task Extensions
    public static void Forget(this Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted) _ = task.Exception; // Observe exception
            return;
        }

        _ = task.ContinueWith(t => { _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
    #endregion

    #region Window Extensions
    public static string GetWindowTitle(this IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetWindowClassName(this IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool IsVisible(this IntPtr hWnd) => NativeMethods.IsWindowVisible(hWnd);

    public static Process? GetProcess(this IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0) return null;
        try { return Process.GetProcessById((int)pid); } catch { return null; }
    }

    public static bool TryGetProcess(this IntPtr hWnd, out Process? process)
    {
        process = hWnd.GetProcess();
        return process != null;
    }
    #endregion

    #region Process Extensions
    public static string? GetCommandLine(this Process process)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            using var objects = searcher.Get();
            foreach (var obj in (ManagementObjectCollection)objects)
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch { }
        return null;
    }

    public static bool TryGetCommandLine(this Process process, out string? commandLine)
    {
        commandLine = process.GetCommandLine();
        return commandLine != null;
    }

    public static Process? GetForegroundProcess()
    {
        var hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return null;
        return hWnd.GetProcess();
    }
    #endregion

    #region String Extensions
    public static bool TryParseResolution(this string? input, out int width, out int height)
    {
        width = 0; height = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = input.Split('x');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }

    public static bool TryParsePosition(this string? input, out int x, out int y)
    {
        x = 0; y = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var parts = input.Split(',');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }

    public static string ToSafeMachineName(this string? machineName)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return "unknown_pc";
        return machineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
    }

    public static string ToFriendlyName(this string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
        return Regex.Replace(text, "(?<=.)([A-Z])", " $1");
    }

    public static string Quote(this string? text) => $"\"{text ?? string.Empty}\"";

    public static string SurroundWith(this string text, string surrounds) => $"{surrounds}{text}{surrounds}";

    public static string SurroundWith(this string text, string starts, string ends) => $"{starts}{text}{ends}";

    public static bool ToBoolean(this string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trueValues = new[] { "true", "ok", "yes", "1", "y", "enabled", "on" };
        return trueValues.Contains(input.ToLowerInvariant());
    }

    public static string ToEnvKey(this string? key, string? prefix = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return key ?? string.Empty;
        var envKey = key.ToUpperInvariant().Replace("-", "_").Replace(":", "_");
        return prefix != null ? $"{prefix}{envKey}" : envKey;
    }
    #endregion

    #region Boolean Extensions
    public static string ToYesNo(this bool input) => input ? "Yes" : "No";
    public static string ToEnabledDisabled(this bool input) => input ? "Enabled" : "Disabled";
    public static string ToOnOff(this bool input) => input ? "On" : "Off";
    #endregion

    #region DateTime / TimeSpan Extensions
    public static bool ExpiredSince(this DateTime dateTime, int minutes) => (DateTime.Now - dateTime).TotalMinutes > minutes;
    public static TimeSpan StripMilliseconds(this TimeSpan time) => new TimeSpan(time.Days, time.Hours, time.Minutes, time.Seconds);
    #endregion

    #region Collection Extensions
    public static void ForEach<T>(this IEnumerable<T> enumerable, Action<T> action)
    {
        foreach (var item in enumerable) action(item);
    }

    public static IEnumerable<(T item, int index)> WithIndex<T>(this IEnumerable<T> self) =>
        self.Select((item, index) => (item, index));
    #endregion

    #region Dictionary Extensions
    public static string GetFriendlyMonitorName(this Dictionary<string, string> dict)
    {
        if (dict.TryGetValue("friendly_name", out var val) && !string.IsNullOrWhiteSpace(val)) return val;
        if (dict.TryGetValue("monitor_name", out val) && !string.IsNullOrWhiteSpace(val)) return val;
        if (dict.TryGetValue("short_monitor_id", out val) && !string.IsNullOrWhiteSpace(val)) return val;
        if (dict.TryGetValue("monitor_serial_number", out val) && !string.IsNullOrWhiteSpace(val)) return val;
        if (dict.TryGetValue("monitor_string", out val) && !string.IsNullOrWhiteSpace(val)) return val;
        if (dict.TryGetValue("name", out val) && !string.IsNullOrWhiteSpace(val)) 
        {
            return val.Replace("\\", "").Replace(".", "");
        }
        return "Unknown Monitor";
    }
    #endregion

    #region JSON Extensions
    public static string GetStringOrDefault(this JsonElement element, string propertyName, string defaultValue = "")
    {
        if (element.TryGetProperty(propertyName, out var prop))
            return prop.GetString() ?? defaultValue;
        return defaultValue;
    }

    public static bool GetBooleanOrDefault(this JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.TryGetProperty(propertyName, out var prop))
            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => defaultValue
            };
        return defaultValue;
    }

    public static int GetIntOrDefault(this JsonElement element, string propertyName, int defaultValue = 0)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.TryGetInt32(out var val))
            return val;
        return defaultValue;
    }
    #endregion

    #region Configuration Extensions
    public static string? GetWithAliases(this IConfiguration config, params string[] keys)
    {
        foreach (var key in keys)
        {
            var val = config[$"MqttAgent:{key}"] ?? config[key] ?? config[key.ToLowerInvariant()];
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return null;
    }

    public static string? GetArgValue(this IEnumerable<string> args, string arg)
    {
        var argList = args.ToList();
        var lowerArg = arg.ToLowerInvariant();
        var index = argList.FindIndex(a => a.Equals(lowerArg, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index < argList.Count - 1) return argList[index + 1];

        var prefixed = argList.FirstOrDefault(a => a.StartsWith($"{lowerArg}:", StringComparison.OrdinalIgnoreCase));
        return prefixed?.Split(':', 2).LastOrDefault();
    }

    public static bool HasArg(this IEnumerable<string> args, string arg)
    {
        return args.Any(a => a.Equals(arg, StringComparison.OrdinalIgnoreCase));
    }
    #endregion
}
