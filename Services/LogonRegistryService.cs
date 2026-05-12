using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System;
using MqttAgent.Utils;

namespace MqttAgent.Services;

public class LogonRegistryService
{
    private readonly RegistryService _registryService;
    private readonly ProcessService _processService;

    public LogonRegistryService(RegistryService registryService, ProcessService processService)
    {
        _registryService = registryService;
        _processService = processService;
    }

    public string Login(string username, string password, string domain = "", bool keepCredentials = false, bool wtsConnect = false)
    {
        try
        {
            // 0. Registry cleanup handled by permanent machine-level task IpcMcp_LogonCleanup
            // (SetupLogonCleanupTask is no longer called here to avoid overwriting current task)

            // 1. Stage Credentials in HKLM Registry
            const string winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            _registryService.WriteRegistry(winlogonPath, "AutoAdminLogon", "1", "String", "HKLM");
            _registryService.WriteRegistry(winlogonPath, "ForceAutoLogon", "1", "String", "HKLM");
            _registryService.WriteRegistry(winlogonPath, "DefaultUserName", username, "String", "HKLM");
            _registryService.WriteRegistry(winlogonPath, "DefaultPassword", password, "String", "HKLM");
            _registryService.WriteRegistry(winlogonPath, "DefaultDomainName", domain, "String", "HKLM");
            
            // Bypass the modern sign-in requirement if possible
            const string passwordlessPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";
            _registryService.WriteRegistry(passwordlessPath, "DevicePasswordLessBuildVersion", "0", "DWord", "HKLM");

            // 2. Refresh Logon UI
            var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
            var processes = Process.GetProcessesByName("LogonUI");
            
            bool logonUIFound = false;
            foreach (var proc in processes)
            {
                if (proc.SessionId == (int)sessionId)
                {
                    proc.Kill();
                    logonUIFound = true;
                }
            }

            string result = logonUIFound 
                ? $"Logon initiated for '{username}' via LogonUI restart." 
                : $"Credentials staged for '{username}', but no active LogonUI found.";

            if (!keepCredentials)
            {
                result += " (Note: keepCredentials=false; registry password may persist if logon fails)";
            }

            if (wtsConnect)
            {
                Task.Run(async () => {
                    await Task.Delay(5000);
                    NativeMethods.WTSConnectSession(sessionId, 0xFFFFFFFF, password, false);
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            throw new Exception($"Login failed: {ex.Message}");
        }
    }

    public async Task<string> TypeLogon(string text, bool enter = true)
    {
        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        Console.WriteLine($"Initiating TypeLogon in session {sessionId}...");
        
        try
        {
            // Escape any special characters for powershell/sendkeys
            var escaped = text.Replace("\"", "`\"").Replace("'", "''");
            var script = $"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{escaped}')";
            if (enter) script += "; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')";
            
            await _processService.StartProcess("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", asUser: sessionId.ToString());
            
            return $"Typed logon text triggered in session {sessionId}.";
        }
        catch (Exception ex)
        {
            throw new Exception($"TypeLogon failed: {ex.Message}");
        }
    }

    public async Task<string> ClearCredentials()
    {
        try
        {
            const string winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            _registryService.WriteRegistry(winlogonPath, "AutoAdminLogon", "0", "String", "HKLM");
            _registryService.WriteRegistry(winlogonPath, "ForceAutoLogon", "0", "String", "HKLM");
            
            // Delete sensitive values
            try { 
                await _processService.StartProcess("reg.exe", $@"delete ""HKLM\{winlogonPath}"" /v ""DefaultPassword"" /f", shellExecute: true);
            } catch { }

            return "Logon credentials cleared from registry.";
        }
        catch (Exception ex)
        {
            throw new Exception($"ClearCredentials failed: {ex.Message}");
        }
    }

    private async Task SetupLogonCleanupTask()
    {
        try
        {
            // This task will run on any logon to ensure the registry is cleared of auto-logon data immediately
            // Path must be carefully quoted for reg delete
            var regCmd = @"reg add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v ""AutoAdminLogon"" /t REG_SZ /d ""0"" /f " +
                         @"& reg delete ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v ""DefaultPassword"" /f " +
                         @"& reg add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v ""ForceAutoLogon"" /t REG_SZ /d ""0"" /f";

            var schArgs = $"/Create /TN \"IpcMcp_LogonCleanup\" /TR \"cmd.exe /c {regCmd}\" /SC ONLOGON /RL HIGHEST /f";
            
            await _processService.StartProcess("schtasks.exe", schArgs, shellExecute: true);
            Console.WriteLine("Logon cleanup task registered successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Failed to register logon cleanup task: {ex.Message}");
        }
    }

    public List<UserAccountInfo> ListUsers()
    {
        var users = new List<UserAccountInfo>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, Domain, LocalAccount, AccountType FROM Win32_UserAccount");
            
            foreach (var user in searcher.Get())
            {
                var name = user["Name"]?.ToString() ?? "";
                var domain = user["Domain"]?.ToString() ?? "";
                var isLocal = (bool)(user["LocalAccount"] ?? false);
                
                users.Add(new UserAccountInfo { 
                    Username = name, 
                    Type = isLocal ? "LocalAccount" : "Remote/Domain", 
                    Domain = domain 
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Dynamic user discovery failed: {ex.Message}. Falling back to essentials.");
            users.Add(new UserAccountInfo { Username = "bluscream", Type = "LocalAccount", Domain = Global.MachineName });
        }
        return users;
    }
}

public class UserAccountInfo
{
    public string Username { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
}
