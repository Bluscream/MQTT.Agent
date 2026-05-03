using System;
using System.Collections.Generic;
using System.Management;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MqttAgent.Services;

public class DeviceService
{
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

        foreach (var id in onlyDisable)
        {
            results.Add(await RunPnpAction(id, "Disable"));
        }

        foreach (var id in onlyEnable)
        {
            results.Add(await RunPnpAction(id, "Enable"));
        }

        foreach (var id in restart)
        {
            results.Add($"Restarting '{id}': " + await RunPnpAction(id, "Disable"));
            await Task.Delay(2000);
            results.Add(await RunPnpAction(id, "Enable"));
        }

        return JsonSerializer.Serialize(new { results });
    }

    private async Task<string> RunPnpAction(string identifier, string action)
    {
        try
        {
            bool isId = identifier.Contains("\\") || identifier.Contains("&");
            string selector = isId ? $"-InstanceId '{identifier}'" : $"-FriendlyName '*{identifier}*'";
            string script = $"$dev = Get-PnpDevice {selector} | Select-Object -First 1; if ($dev) {{ {action}-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false; \"OK\" }} else {{ \"NotFound\" }}";

            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            output = output.Trim();
            if (output == "OK") return $"Device '{identifier}' {action}d successfully.";
            if (output == "NotFound") return $"Could not find device matching '{identifier}'.";
            return $"Error performing {action} on '{identifier}': {output} {error}";
        }
        catch (Exception ex)
        {
            return $"Failed to {action} device '{identifier}': {ex.Message}";
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
