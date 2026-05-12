using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MqttAgent.Utils;
using System.Collections.Generic;
using System.Linq;

namespace MqttAgent.Services;

public class ProcessService
{
    // Windows API P/Invoke declarations for user session and elevation
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    private bool EnablePrivilege(string privilegeName)
    {
        IntPtr hToken = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(GetCurrentProcess(), NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY, out hToken))
                return false;

            NativeMethods.LUID luid = new NativeMethods.LUID();
            if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out luid))
                return false;

            NativeMethods.TOKEN_PRIVILEGES tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new NativeMethods.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = NativeMethods.SE_PRIVILEGE_ENABLED
                }
            };

            var result = NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();
            if (!result || error != 0)
            {
                Console.WriteLine($"[ProcessService] AdjustTokenPrivileges({privilegeName}) Result: {result}, Error: {error}");
            }
            return result && error == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessService] EnablePrivilege Exception: {ex.Message}");
            return false;
        }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType,
        out IntPtr phNewToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous = 0,
        SecurityIdentification = 1,
        SecurityImpersonation = 2,
        SecurityDelegation = 3
    }

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation = 2
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    private ProcessWindowStyle ParseWindowStyle(string? windowStyle)
    {
        if (string.IsNullOrWhiteSpace(windowStyle))
        {
            return ProcessWindowStyle.Normal;
        }

        return windowStyle.Trim().ToLowerInvariant() switch
        {
            "hidden" => ProcessWindowStyle.Hidden,
            "minimized" => ProcessWindowStyle.Minimized,
            "maximized" => ProcessWindowStyle.Maximized,
            "normal" => ProcessWindowStyle.Normal,
            _ => ProcessWindowStyle.Normal
        };
    }

    private async Task WaitForProcessExit(Process process, int timeoutMs, string processName)
    {
        if (timeoutMs == -1)
        {
            // Infinite wait - no timeout, use WaitForExitAsync for proper async handling
            await process.WaitForExitAsync();
        }
        else
        {
            // Use timeout as safety measure
            var waitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(waitTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                // Timeout occurred - kill the process
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Process might have already exited
                }
                
                // Wait a moment for kill to take effect
                await Task.Delay(100);
                
                throw new TimeoutException($"Process '{processName}' timed out after {timeoutMs}ms");
            }
            
            // Process exited normally - ensure waitTask completed
            await waitTask;
        }
    }

    private uint GetUserSessionId(string asUser)
    {
        // Try to parse as session ID (number)
        if (uint.TryParse(asUser, out uint sessionId))
        {
            return sessionId;
        }

        // Otherwise, treat as username and find their session
        try
        {
            // Find explorer.exe process for the user
            var explorerProcesses = Process.GetProcessesByName("explorer");
            foreach (var proc in explorerProcesses)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        $"SELECT * FROM Win32_Process WHERE ProcessId = {proc.Id}");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var owner = obj.InvokeMethod("GetOwner", null);
                        if (owner != null)
                        {
                            var ownerInfo = (ManagementBaseObject)owner;
                            var username = ownerInfo["User"]?.ToString();
                            if (username != null && username.Equals(asUser, StringComparison.OrdinalIgnoreCase))
                            {
                                return (uint)proc.SessionId;
                            }
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }

            // Fallback: try active console session
            var activeSession = NativeMethods.WTSGetActiveConsoleSessionId();
            if (activeSession != 0)
            {
                return activeSession;
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to find session for user '{asUser}': {ex.Message}");
        }

        throw new Exception($"Could not find active session for user '{asUser}'");
    }

    public uint GetActiveConsoleSessionId()
    {
        return NativeMethods.WTSGetActiveConsoleSessionId();
    }

    private Process CreateProcessInternal(IntPtr? token, string command, string arguments, out int processId, string desktop = "winsta0\\default", ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal)
    {
        short showWindow = 1; // SW_SHOWNORMAL
        if (windowStyle == ProcessWindowStyle.Hidden) showWindow = 0; // SW_HIDE
        else if (windowStyle == ProcessWindowStyle.Minimized) showWindow = 2; // SW_SHOWMINIMIZED
        else if (windowStyle == ProcessWindowStyle.Maximized) showWindow = 3; // SW_SHOWMAXIMIZED

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf(typeof(STARTUPINFO)),
            lpReserved = "",
            lpDesktop = desktop,
            lpTitle = "",
            dwFlags = 0x00000001, // STARTF_USESHOWWINDOW
            wShowWindow = showWindow,
            cbReserved2 = 0,
            lpReserved2 = IntPtr.Zero,
            hStdInput = IntPtr.Zero,
            hStdOutput = IntPtr.Zero,
            hStdError = IntPtr.Zero
        };

        var processInfo = new PROCESS_INFORMATION();
        var commandLine = $"\"{command}\" {arguments}";

        bool success;
        if (token.HasValue && token.Value != IntPtr.Zero)
        {
            success = CreateProcessAsUser(
                token.Value,
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out processInfo);
        }
        else
        {
            success = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out processInfo);
        }

        if (!success)
        {
            throw new Exception($"Failed to {(token.HasValue ? "CreateProcessAsUser" : "CreateProcess")}. Error: {Marshal.GetLastWin32Error()}");
        }

        processId = processInfo.dwProcessId;
        CloseHandle(processInfo.hThread);
        
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (Exception ex)
        {
            throw new Exception($"Process created with PID {processId} but cannot access Process object: {ex.Message}");
        }
    }

    public async Task<string> StartProcess(string executable, string? arguments = null, bool waitForExit = false, int timeoutMs = 30000, bool shellExecute = false, string? asUser = null, bool elevated = false, string? windowStyle = null, string? desktop = null)
    {
        try
        {
            // Validate that asUser and elevated are not both specified
            if (!string.IsNullOrEmpty(asUser) && elevated)
            {
                throw new ArgumentException("Cannot specify both asUser and elevated parameters. Choose one.");
            }

            Process? process = null;
            IntPtr userToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            int? createdProcessId = null;

            try
            {
                // Handle running as specific user
                if (!string.IsNullOrEmpty(asUser))
                {
                    uint sessionId = GetUserSessionId(asUser);
                    
                    // Enable SeTcbPrivilege to allow WTSQueryUserToken to succeed when running as SYSTEM
                    if (!EnablePrivilege(NativeMethods.SE_TCB_NAME))
                    {
                        // Log but don't fail, maybe we already have it or don't need it (if not running as SYSTEM)
                    }

                    if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken))
                    {
                        throw new Exception($"Failed to get user token for session {sessionId}. Error: {Marshal.GetLastWin32Error()}");
                    }

                    // Duplicate token to primary token for CreateProcessAsUser
                    if (!DuplicateTokenEx(userToken, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY,
                        IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                        TOKEN_TYPE.TokenPrimary, out primaryToken))
                    {
                        throw new Exception($"Failed to duplicate token. Error: {Marshal.GetLastWin32Error()}");
                    }

                    // Create process as user - store process ID separately
                    var desktopStr = desktop ?? "winsta0\\default";
                    var parsedWindowStyle = ParseWindowStyle(windowStyle);
                    process = CreateProcessInternal(primaryToken, executable, arguments ?? "", out int processId, desktopStr, parsedWindowStyle);
                    createdProcessId = processId;
                }
                else if (elevated)
                {
                    // Run elevated using UAC prompt
                    var parsedWindowStyle = ParseWindowStyle(windowStyle);
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = arguments ?? "",
                        UseShellExecute = true,
                        Verb = "runas",
                        CreateNoWindow = !shellExecute, // Show window if not shellExecute mode
                        WindowStyle = parsedWindowStyle
                    };

                    process = Process.Start(processInfo);
                    if (process == null)
                    {
                        throw new Exception("Failed to start elevated process");
                    }

                    // For elevated processes, only wait if waitForExit is true
                    if (waitForExit)
                    {
                        await WaitForProcessExit(process, timeoutMs, executable);
                        return $"Elevated process '{executable}' started and exited with code {process.ExitCode}";
                    }

                    return $"Elevated process '{executable}' started successfully (PID: {process.Id})";
                }
                else if (!string.IsNullOrEmpty(desktop))
                {
                    // For running on a specific desktop without impersonation (e.g. SYSTEM on Winlogon)
                    var parsedWindowStyle = ParseWindowStyle(windowStyle);
                    process = CreateProcessInternal(null, executable, arguments ?? "", out int processId, desktop, parsedWindowStyle);
                    createdProcessId = processId;
                }
                else
                {
                    // Standard execution - choose mode based on shellExecute parameter
                    if (shellExecute)
                    {
                        // ShellExecute mode: redirect output, wait, return output
                        var parsedWindowStyle = ParseWindowStyle(windowStyle);
                        var processInfo = new ProcessStartInfo
                        {
                            FileName = executable,
                            Arguments = arguments ?? "",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                            WindowStyle = parsedWindowStyle
                        };

                        process = new Process { StartInfo = processInfo };
                    }
                    else
                    {
                        // StartProcess mode: use shell execute, visible window
                        var parsedWindowStyle = ParseWindowStyle(windowStyle);
                        var processInfo = new ProcessStartInfo
                        {
                            FileName = executable,
                            Arguments = arguments ?? "",
                            UseShellExecute = true,
                            CreateNoWindow = false,
                            WindowStyle = parsedWindowStyle
                        };

                        process = Process.Start(processInfo);
                        if (process == null)
                        {
                            throw new Exception($"Failed to start process '{executable}'");
                        }
                    }
                }

                if (process == null)
                {
                    throw new Exception("Failed to create process");
                }

                // Handle output redirection for shellExecute mode
                if (shellExecute && string.IsNullOrEmpty(asUser) && !elevated && string.IsNullOrEmpty(desktop))
                {
                    // Standard shellExecute mode with output redirection
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();

                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                            outputBuilder.AppendLine(e.Data);
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null)
                            errorBuilder.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await WaitForProcessExit(process, timeoutMs, executable);

                    var output = outputBuilder.ToString().TrimEnd();
                    var error = errorBuilder.ToString().TrimEnd();

                    if (process.ExitCode != 0)
                    {
                        var errorMsg = string.IsNullOrEmpty(error) ? "Command failed" : error;
                        return $"Exit code: {process.ExitCode}\n{errorMsg}\n{output}";
                    }

                    return string.IsNullOrEmpty(output) ? "Command executed successfully (no output)" : output;
                }
                else if (!string.IsNullOrEmpty(asUser) || !string.IsNullOrEmpty(desktop))
                {
                    // asUser or desktop mode - can't redirect output, optionally wait
                    // Use stored process ID instead of accessing process.Id which may fail
                    int processId = createdProcessId ?? process.Id;
                    
                    if (waitForExit)
                    {
                        await WaitForProcessExit(process, timeoutMs, executable);
                        // Try to get exit code, but handle gracefully if it's not accessible
                        try
                        {
                            int exitCode = process.ExitCode;
                            return $"Process '{executable}' started and exited with code {exitCode}";
                        }
                        catch
                        {
                            return $"Process '{executable}' started and exited (PID: {processId})";
                        }
                    }
                    return $"Process '{executable}' started successfully (PID: {processId})";
                }
                else
                {
                    // Standard StartProcess mode
                    if (waitForExit)
                    {
                        await WaitForProcessExit(process, timeoutMs, executable);
                        return $"Process '{executable}' started and exited with code {process.ExitCode}";
                    }

                    return $"Process '{executable}' started successfully (PID: {process.Id})";
                }
            }
            finally
            {
                if (primaryToken != IntPtr.Zero)
                    CloseHandle(primaryToken);
                if (userToken != IntPtr.Zero)
                    CloseHandle(userToken);
            }
        }
        catch (OperationCanceledException) when (timeoutMs != -1)
        {
            throw new TimeoutException($"Process '{executable}' timed out after {timeoutMs}ms");
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to start application '{executable}': {ex.Message}");
        }
    }

    public string ListProcesses(int timeoutMs = 30000)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var processes = Process.GetProcesses();
            var result = new StringBuilder();
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            
            foreach (var process in processes.OrderBy(p => p.ProcessName))
            {
                // Check timeout periodically
                if ((DateTime.UtcNow - startTime) > timeout)
                {
                    throw new TimeoutException($"Listing processes timed out after {timeoutMs}ms");
                }
                
                try
                {
                    var processName = process.ProcessName;
                    var pid = process.Id;
                    string commandLine = "";
                    
                    try
                    {
                        // Try to get command line using WMI (more reliable)
                        using var searcher = new ManagementObjectSearcher(
                            $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            commandLine = obj["CommandLine"]?.ToString() ?? "";
                            break;
                        }
                    }
                    catch
                    {
                        // Fallback: try to get main module path
                        try
                        {
                            commandLine = process.MainModule?.FileName ?? "";
                        }
                        catch
                        {
                            commandLine = "N/A (access denied)";
                        }
                    }
                    
                    result.AppendLine($"{pid}\t{processName}\t{commandLine}");
                }
                catch
                {
                    // Skip processes we can't access
                    continue;
                }
            }
            
            return result.ToString().TrimEnd();
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to list processes: {ex.Message}");
        }
    }

    public string KillProcess(List<string>? names = null, List<int>? ids = null)
    {
        try
        {
            var processes = new List<Process>();
            
            // Collect processes by names
            if (names != null && names.Count > 0)
            {
                foreach (var name in names)
                {
                    var processName = name.Replace(".exe", "");
                    var foundProcesses = Process.GetProcessesByName(processName);
                    processes.AddRange(foundProcesses);
                }
            }
            
            // Collect processes by IDs
            if (ids != null && ids.Count > 0)
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var process = Process.GetProcessById(id);
                        if (!processes.Any(p => p.Id == id))
                        {
                            processes.Add(process);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process doesn't exist, skip it
                    }
                }
            }
            
            if (processes.Count == 0)
            {
                return "No processes found to kill";
            }
            
            var killed = new List<string>();
            var failed = new List<string>();
            
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    killed.Add($"{process.ProcessName} (PID: {process.Id})");
                }
                catch (Exception ex)
                {
                    failed.Add($"{process.ProcessName} (PID: {process.Id}): {ex.Message}");
                }
            }
            
            var result = new StringBuilder();
            if (killed.Count > 0)
            {
                result.AppendLine($"Successfully killed {killed.Count} process(es):");
                foreach (var item in killed)
                {
                    result.AppendLine($"  - {item}");
                }
            }
            
            if (failed.Count > 0)
            {
                result.AppendLine($"Failed to kill {failed.Count} process(es):");
                foreach (var item in failed)
                {
                    result.AppendLine($"  - {item}");
                }
            }
            
            return result.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to kill processes: {ex.Message}");
        }
    }
}
