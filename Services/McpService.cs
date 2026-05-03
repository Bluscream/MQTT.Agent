using System.Diagnostics;
using System.IO;

namespace MqttAgent.Services;

public class McpService
{
    public string StopMcp()
    {
        Console.WriteLine("CRITICAL: stop_mcp command received. Exiting process...");
        
        // Start a task to exit after returning the result
        Task.Run(async () => {
            await Task.Delay(500);
            Environment.Exit(0);
        });

        return "Stop command received. Service will exit in 500ms.";
    }

    public string RestartMcp()
    {
        Console.WriteLine("CRITICAL: restart_mcp command received. Preparing self-restart...");
        
        try
        {
            var currentPid = Process.GetCurrentProcess().Id;
            var tempScript = Path.Combine(Path.GetTempPath(), $"restart_mcp_{currentPid}.ps1");
            
            var scriptBody = $@"
# Self-restart script for IpcMcp
Start-Sleep -Seconds 1
$service = Get-Service -Name 'IpcMcp' -ErrorAction SilentlyContinue
if ($service) {{
    Stop-Service -Name 'IpcMcp' -Force -ErrorAction SilentlyContinue
}}
# Force kill the process if it's still hung
Get-Process -Id {currentPid} -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if ($service) {{
    Start-Service -Name 'IpcMcp'
}} else {{
    # If not a service, try to restart the binary
    Start-Process -FilePath '{Process.GetCurrentProcess().MainModule?.FileName}'
}}
# Self-destruct
Remove-Item -Path $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue
";

            File.WriteAllText(tempScript, scriptBody);
            
            // Run the script detached
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            
            // Exit current process
            Task.Run(async () => {
                await Task.Delay(500);
                Environment.Exit(0);
            });

            return "Restart command received. Temporary script launched. Service will restart in 500ms.";
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to initiate restart: {ex.Message}");
        }
    }
}
