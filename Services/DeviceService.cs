using System;
using System.Collections.Generic;
using System.Management;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MqttAgent.Utils;
using Microsoft.Extensions.Logging;

namespace MqttAgent.Services;

public class DeviceService
{
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(ILogger<DeviceService> logger)
    {
        _logger = logger;
    }
    public string ListDevices(string[]? categories)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            string query = "SELECT Name, DeviceID, PNPClass, Status, Present FROM Win32_PnPEntity";
            if (categories != null && categories.Length > 0)
            {
                var escapedCategories = categories.Select(c => $"'{c}'");
                query += $" WHERE PNPClass IN ({string.Join(",", escapedCategories)})";
            }

            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", query);
            foreach (var obj in searcher.Get())
            {
                devices.Add(new DeviceInfo
                {
                    Name = obj["Name"]?.ToString() ?? "",
                    DeviceID = obj["DeviceID"]?.ToString() ?? "",
                    Class = obj["PNPClass"]?.ToString() ?? "",
                    Status = obj["Status"]?.ToString() ?? "",
                    Present = (bool)(obj["Present"] ?? false)
                });
            }
        }
        catch (Exception ex)
        {
             return JsonSerializer.Serialize(new { error = ex.Message });
        }

        return JsonSerializer.Serialize(devices); // Compact JSON
    }

    public async Task<string> ToggleDevices(string[]? enable, string[]? disable)
    {
        var results = new List<string>();
        
        enable ??= Array.Empty<string>();
        disable ??= Array.Empty<string>();

        var restart = enable.Intersect(disable, StringComparer.OrdinalIgnoreCase).ToArray();
        var onlyEnable = enable.Except(restart, StringComparer.OrdinalIgnoreCase).ToArray();
        var onlyDisable = disable.Except(restart, StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (var pattern in onlyDisable)
        {
            results.AddRange(await RunPnpAction(pattern, "Disable"));
        }

        foreach (var pattern in onlyEnable)
        {
            results.AddRange(await RunPnpAction(pattern, "Enable"));
        }

        foreach (var pattern in restart)
        {
            results.Add($"--- Restarting devices matching '{pattern}' ---");
            var matches = await ResolveDevices(pattern);
            if (matches.Count == 0)
            {
                results.Add($"Could not find any devices matching '{pattern}'.");
                continue;
            }

            foreach (var dev in matches)
            {
                results.Add(await SetDeviceState(dev.DeviceID, dev.Name, "Disable"));
                await Task.Delay(1000);
                results.Add(await SetDeviceState(dev.DeviceID, dev.Name, "Enable"));
            }
        }

        return JsonSerializer.Serialize(new { results });
    }

    private async Task<List<DeviceInfo>> ResolveDevices(string pattern)
    {
        var devices = new List<DeviceInfo>();
        try
        {
            // Translate glob * to WMI %
            string wmiPattern = pattern.Replace("*", "%").Replace("'", "''");
            if (!wmiPattern.Contains("%")) wmiPattern = $"%{wmiPattern}%";

            string query = $"SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '{wmiPattern}' OR DeviceID LIKE '{wmiPattern}'";
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", query);
            
            foreach (var obj in searcher.Get())
            {
                devices.Add(new DeviceInfo
                {
                    Name = obj["Name"]?.ToString() ?? "Unknown",
                    DeviceID = obj["DeviceID"]?.ToString() ?? ""
                });
            }
        }
        catch (Exception)
        {
            _logger.LogError("Failed to resolve devices with pattern {Pattern}", pattern);
        }
        return devices;
    }

    private async Task<List<string>> RunPnpAction(string pattern, string action)
    {
        var results = new List<string>();
        var devices = await ResolveDevices(pattern);

        if (devices.Count == 0)
        {
            results.Add($"Could not find any devices matching '{pattern}'.");
            return results;
        }

        foreach (var dev in devices)
        {
            results.Add(await SetDeviceState(dev.DeviceID, dev.Name, action));
        }

        return results;
    }

    private async Task<string> SetDeviceState(string instanceId, string name, string action)
    {
        try
        {
            bool enable = action.Equals("Enable", StringComparison.OrdinalIgnoreCase);
            bool success = PnpHelper.SetDeviceState(instanceId, enable);

            if (success)
            {
                return $"Device '{name}' ({instanceId}) {action}d successfully.";
            }
            else
            {
                return $"Failed to {action} device '{name}' ({instanceId}). Native error.";
            }
        }
        catch (Exception ex)
        {
            return $"Failed to {action} device '{name}': {ex.Message}";
        }
    }
}

public class DeviceInfo
{
    public string Name { get; set; } = "";
    public string DeviceID { get; set; } = "";
    public string Class { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Present { get; set; }
}
