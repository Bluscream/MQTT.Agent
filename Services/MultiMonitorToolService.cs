using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Text.Json;
using MqttAgent.Utils;

namespace MqttAgent.Services;

public class MultiMonitorToolService
{
    private readonly ProcessService _processService;
    private readonly DeviceService _deviceService;
    private string? _cachedToolPath;
    private readonly string _tempDir;
    private readonly string _toolFile;

    public MultiMonitorToolService(ProcessService processService, DeviceService deviceService)
    {
        _processService = processService;
        _deviceService = deviceService;
        
        // Use application-relative path for extraction to ensure Session 1 user can execute it
        _tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        _toolFile = Path.Combine(_tempDir, "MultiMonitorTool.exe");
    }

    private string GetToolPath()
    {
        if (_cachedToolPath != null && File.Exists(_cachedToolPath))
            return _cachedToolPath;

        // 1. Check current directory
        var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MultiMonitorTool.exe");
        if (File.Exists(localPath))
        {
            _cachedToolPath = localPath;
            return _cachedToolPath;
        }

        // 2. Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var path in pathEnv.Split(';'))
            {
                var fullPath = Path.Combine(path, "MultiMonitorTool.exe");
                if (File.Exists(fullPath))
                {
                    _cachedToolPath = fullPath;
                    return _cachedToolPath;
                }
            }
        }

        // 3. Fallback to embedded resource
        if (!File.Exists(_toolFile))
        {
            ExtractEmbeddedResource();
        }

        if (File.Exists(_toolFile))
        {
            _cachedToolPath = _toolFile;
            return _cachedToolPath;
        }

        throw new FileNotFoundException("MultiMonitorTool.exe not found in application directory, PATH, or embedded resources.");
    }

    private void ExtractEmbeddedResource()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            // Look for a resource ending in MultiMonitorTool.exe
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("MultiMonitorTool.exe", StringComparison.OrdinalIgnoreCase));
            
            if (resourceName != null)
            {
                if (!Directory.Exists(_tempDir)) Directory.CreateDirectory(_tempDir);

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var fileStream = File.Create(_toolFile);
                    stream.CopyTo(fileStream);
                    Console.WriteLine($"[MultiMonitorToolService] Extracted tool to {_toolFile}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiMonitorToolService] Failed to extract embedded resource: {ex.Message}");
        }
    }

    public async Task<string> GetMonitorsAsync(string format, string? delimiter = null, string? layout = null)
    {
        // Use a shared temp directory accessible to both SYSTEM and interactive User
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var tempDir = Path.Combine(baseDir, "temp");
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
        
        var tempFile = Path.Combine(tempDir, $"monitors_{Guid.NewGuid()}.tmp");
        var actualFormat = format.ToLower();
        var useJson = actualFormat == "json";
        
        // If JSON, we use XML as the base to parse
        var toolFormat = useJson ? "xml" : actualFormat;
        var argPrefix = toolFormat switch
        {
            "xml" => "/sxml",
            "html" => "/shtml",
            "csv" => delimiter?.ToLower() == "comma" ? "/scomma" : "/stab",
            "text" or "txt" => "/stext",
            _ => "/shtml"
        };

        var args = new List<string> { argPrefix, $"\"{tempFile}\"" };
        
        if (actualFormat == "html" && layout?.ToLower() == "vertical")
        {
            args.Add("/html_layout 2");
        }

        try
        {
            await RunToolAsync(args.ToArray());
            
            if (!File.Exists(tempFile))
            {
                Console.WriteLine($"[MultiMonitorToolService] MultiMonitorTool did not create the output file at {tempFile}");
                return useJson ? "[]" : $"Error: MultiMonitorTool did not create the output file at {tempFile}";
            }

            var content = await File.ReadAllTextAsync(tempFile);
            File.Delete(tempFile);

            if (useJson)
            {
                var json = ConvertXmlToJson(content);
                // Fallback to DeviceService if MMT returned empty (e.g. tool failed to see monitors)
                if (string.IsNullOrEmpty(json) || json.Trim() == "[]")
                {
                    Console.WriteLine("[MultiMonitorToolService] MMT returned empty JSON. Falling back to DeviceService.");
                    var pnpJson = _deviceService.ListDevices(new[] { "Monitor" });
                    var pnpDevices = JsonSerializer.Deserialize<List<DeviceInfo>>(pnpJson);
                    if (pnpDevices != null)
                    {
                        var mapped = pnpDevices.Select(d => {
                            var dict = new Dictionary<string, string> {
                                { "name", d.Name },
                                { "monitor_id", d.DeviceID },
                                { "monitor_name", d.Name },
                                { "status", d.Status },
                                { "present", d.Present.ToString() }
                            };
                            dict["friendly_name"] = dict.GetFriendlyMonitorName();
                            return dict;
                        });
                        return JsonSerializer.Serialize(mapped, new JsonSerializerOptions { WriteIndented = true });
                    }
                    return pnpJson;
                }
                return json;
            }

            return content;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiMonitorToolService] GetMonitorsAsync Exception: {ex.Message}");
            return useJson ? "[]" : $"Error: {ex.Message}";
        }
    }

    public async Task<string> RunCommandAsync(string command, params string[] args)
    {
        var allArgs = new List<string> { $"/{command}" };
        allArgs.AddRange(args);
        return await RunToolAsync(allArgs.ToArray());
    }

    private async Task<string> RunToolAsync(params string[] args)
    {
        var toolPath = GetToolPath();
        var arguments = string.Join(" ", args);
        var sessionId = _processService.GetActiveConsoleSessionId();

        // If we are running in session 0 (as a service), try to run as the interactive user to see monitors
        if (sessionId != 0 && sessionId != 0xFFFFFFFF)
        {
            Console.WriteLine($"[MultiMonitorToolService] Running tool as session {sessionId}: {toolPath} {arguments}");
            return await _processService.StartProcess(toolPath, arguments, waitForExit: true, asUser: sessionId.ToString());
        }

        // Fallback to standard process start if no active session
        var startInfo = new ProcessStartInfo
        {
            FileName = toolPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var err = error.ToString();
            throw new Exception($"MultiMonitorTool exited with code {process.ExitCode}. Error: {err}");
        }

        return output.ToString();
    }

    private string ConvertXmlToJson(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            var monitors = doc.Descendants("item").Select(item => {
                var dict = item.Elements()
                    .Where(e => !string.IsNullOrWhiteSpace(e.Value))
                    .ToDictionary(e => e.Name.LocalName, e => e.Value);
                
                dict["friendly_name"] = dict.GetFriendlyMonitorName();
                return dict;
            }).ToList();

            return JsonSerializer.Serialize(monitors, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MultiMonitorToolService] Error parsing XML to JSON: {ex.Message}");
            return "[]";
        }
    }

    // Wrapper methods for common commands
    public Task<string> EnableAsync(string monitor) => RunCommandAsync("enable", $"\"{monitor}\"");
    public Task<string> DisableAsync(string monitor) => RunCommandAsync("disable", $"\"{monitor}\"");
    public Task<string> SwitchAsync(string monitor) => RunCommandAsync("switch", $"\"{monitor}\"");
    public Task<string> SetPrimaryAsync(string monitor) => RunCommandAsync("setprimary", $"\"{monitor}\"");
    public Task<string> SetOrientationAsync(string monitor, int orientation) => RunCommandAsync("SetOrientation", $"\"{monitor}\"", orientation.ToString());
    public Task<string> SetScaleAsync(string monitor, int scale) => RunCommandAsync("SetScale", $"\"{monitor}\"", scale.ToString());
    public Task<string> SetMaxResolutionAsync(string monitor) => RunCommandAsync("setmax", $"\"{monitor}\"");
    public Task<string> SetMonitorsAsync(string monitorDetails) => RunCommandAsync("SetMonitors", $"\"{monitorDetails}\"");
    public Task<string> SetResolutionAsync(string monitor, int width, int height) => RunCommandAsync("SetMonitors", $"\"Name={monitor} Width={width} Height={height}\"");
    
    public async Task<string> ResolveMonitorName(string identifier)
    {
        if (identifier.Equals("all", StringComparison.OrdinalIgnoreCase)) return "all";
        if (int.TryParse(identifier, out _)) return identifier;

        var screensJson = await GetMonitorsAsync("json");
        if (!string.IsNullOrEmpty(screensJson))
        {
            try
            {
                var screens = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(screensJson);
                if (screens != null)
                {
                    foreach (var s in screens)
                    {
                        var name = s.GetValueOrDefault("name") ?? "";
                        var friendly = s.GetValueOrDefault("friendly_name") ?? "";
                        var shortId = s.GetValueOrDefault("short_monitor_id") ?? "";
                        
                        if (string.Equals(friendly, identifier, StringComparison.OrdinalIgnoreCase) || 
                            string.Equals(shortId, identifier, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, identifier, StringComparison.OrdinalIgnoreCase))
                        {
                            return name;
                        }
                    }
                }
            }
            catch { }
        }
        return identifier;
    }

    public Task<string> TurnOffMonitorsAsync() => RunCommandAsync("TurnOff");
    public Task<string> TurnOnMonitorsAsync() => RunCommandAsync("TurnOn");
    public Task<string> SwitchOffOnMonitorsAsync() => RunCommandAsync("SwitchOffOn");
    public Task<string> MoveAllWindowsToPrimaryAsync() => RunCommandAsync("MoveWindow", "Primary", "All");
}
