using System;
using System.Diagnostics;
using System.Management;

namespace MqttAgent.Utils;

public static class Extensions
{
    #region Process Extensions
    public static string? TryGetCommandLine(this Process process)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            using var objects = searcher.Get();
            foreach (var obj in objects)
            {
                return obj["CommandLine"]?.ToString();
            }
        }
        catch { }
        return null;
    }
    #endregion
}
