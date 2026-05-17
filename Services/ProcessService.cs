using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MqttAgent.Utils;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace MqttAgent.Services;

public class ProcessService
{
    private readonly ILogger<ProcessService> _logger;

    public ProcessService(ILogger<ProcessService> logger)
    {
        _logger = logger;
    }

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
                _logger.LogWarning("[ProcessService] AdjustTokenPrivileges({Privilege}) Result: {Result}, Error: {Error}", privilegeName, result, error);
            }
            return result && error == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProcessService] EnablePrivilege Exception: {Message}", ex.Message);
            return false;
        }
        finally
        {
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
        }
    }

    private IntPtr TryStealUserToken(uint sessionId)
    {
        var processes = Process.GetProcessesByName("explorer");
        foreach (var p in processes)
        {
            if (p.SessionId == (int)sessionId)
            {
                IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION, false, (uint)p.Id);
                if (hProcess != IntPtr.Zero)
                {
                    IntPtr hToken = IntPtr.Zero;
                    if (NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY, out hToken))
                    {
                        NativeMethods.CloseHandle(hProcess);
                        return hToken;
                    }
                    NativeMethods.CloseHandle(hProcess);
                }
            }
        }
        return IntPtr.Zero;
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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        out IntPtr TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

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
    private const uint CREATE_NO_WINDOW = 0x08000000;

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
            await process.WaitForExitAsync();
        }
        else
        {
            var waitTask = process.WaitForExitAsync();
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(waitTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                try { process.Kill(); } catch { }
                await Task.Delay(100);
                throw new TimeoutException($"Process '{processName}' timed out after {timeoutMs}ms");
            }
            await waitTask;
        }
    }

    private uint GetUserSessionId(string asUser)
    {
        if (uint.TryParse(asUser, out uint sessionId)) return sessionId;

        try
        {
            var explorerProcesses = Process.GetProcessesByName("explorer");
            foreach (var proc in explorerProcesses)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ProcessId = {proc.Id}");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var owner = obj.InvokeMethod("GetOwner", null);
                        if (owner != null)
                        {
                            var username = ((ManagementBaseObject)owner)["User"]?.ToString();
                            if (username != null && username.Equals(asUser, StringComparison.OrdinalIgnoreCase))
                                return (uint)proc.SessionId;
                        }
                    }
                }
                catch { continue; }
            }
            var activeSession = NativeMethods.WTSGetActiveConsoleSessionId();
            if (activeSession != 0) return activeSession;
        }
        catch (Exception ex) { throw new Exception($"Failed to find session for user '{asUser}': {ex.Message}"); }

        throw new Exception($"Could not find active session for user '{asUser}'");
    }

    public uint GetActiveConsoleSessionId() => NativeMethods.WTSGetActiveConsoleSessionId();

    private Process CreateProcessInternal(IntPtr? token, string command, string arguments, out int processId, string desktop = "winsta0\\default", ProcessWindowStyle windowStyle = ProcessWindowStyle.Normal)
    {
        short showWindow = 1;
        if (windowStyle == ProcessWindowStyle.Hidden) showWindow = 0;
        else if (windowStyle == ProcessWindowStyle.Minimized) showWindow = 2;
        else if (windowStyle == ProcessWindowStyle.Maximized) showWindow = 3;

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf(typeof(STARTUPINFO)),
            lpDesktop = desktop,
            dwFlags = 0x00000001,
            wShowWindow = showWindow
        };

        var processInfo = new PROCESS_INFORMATION();
        var commandLine = $"\"{command}\" {arguments}";
        uint creationFlags = CREATE_UNICODE_ENVIRONMENT;
        if (windowStyle == ProcessWindowStyle.Hidden) creationFlags |= CREATE_NO_WINDOW;

        bool success = token.HasValue && token.Value != IntPtr.Zero 
            ? CreateProcessAsUser(token.Value, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, creationFlags, IntPtr.Zero, null, ref startupInfo, out processInfo)
            : CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, creationFlags, IntPtr.Zero, null, ref startupInfo, out processInfo);

        if (!success) throw new Exception($"Failed to start process. Error: {Marshal.GetLastWin32Error()}");

        processId = processInfo.dwProcessId;
        CloseHandle(processInfo.hThread);
        return Process.GetProcessById(processId);
    }

    public async Task<string> StartProcess(string executable, string? arguments = null, bool waitForExit = false, int timeoutMs = 30000, bool shellExecute = false, string? asUser = null, bool elevated = false, string? windowStyle = null, string? desktop = null)
    {
        try
        {
            Process? process = null;
            IntPtr userToken = IntPtr.Zero;
            IntPtr linkedToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            int? createdProcessId = null;

            try
            {
                if (!string.IsNullOrEmpty(asUser))
                {
                    uint sessionId = GetUserSessionId(asUser);
                    EnablePrivilege(NativeMethods.SE_TCB_NAME);
                    if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken)) userToken = TryStealUserToken(sessionId);
                    if (userToken == IntPtr.Zero) throw new Exception($"Failed to get user token for session {sessionId}.");

                    IntPtr tokenToUse = userToken;

                    if (elevated)
                    {
                        uint returnLength = 0;
                        // 19 = TokenLinkedToken
                        if (GetTokenInformation(userToken, 19, out linkedToken, (uint)IntPtr.Size, out returnLength) && linkedToken != IntPtr.Zero)
                        {
                            _logger.LogInformation("[ProcessService] Successfully retrieved elevated Linked Token for user session.");
                            tokenToUse = linkedToken;
                        }
                        else
                        {
                            _logger.LogWarning("[ProcessService] Could not retrieve elevated Linked Token (User may not be an Admin or UAC is disabled). Falling back to standard user token.");
                        }
                    }

                    if (!DuplicateTokenEx(tokenToUse, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY, IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out primaryToken))
                        throw new Exception($"Failed to duplicate token. Error: {Marshal.GetLastWin32Error()}");

                    process = CreateProcessInternal(primaryToken, executable, arguments ?? "", out int pid, desktop ?? "winsta0\\default", ParseWindowStyle(windowStyle));
                    createdProcessId = pid;
                }
                else if (elevated)
                {
                    var processInfo = new ProcessStartInfo { FileName = executable, Arguments = arguments ?? "", UseShellExecute = true, Verb = "runas", WindowStyle = ParseWindowStyle(windowStyle) };
                    process = Process.Start(processInfo);
                    if (process == null) throw new Exception("Failed to start elevated process");
                    if (waitForExit) { await WaitForProcessExit(process, timeoutMs, executable); return $"Elevated process exited with code {process.ExitCode}"; }
                    return $"Elevated process started (PID: {process.Id})";
                }
                else if (!string.IsNullOrEmpty(desktop))
                {
                    process = CreateProcessInternal(null, executable, arguments ?? "", out int pid, desktop, ParseWindowStyle(windowStyle));
                    createdProcessId = pid;
                }
                else
                {
                    if (shellExecute)
                    {
                        var processInfo = new ProcessStartInfo { FileName = executable, Arguments = arguments ?? "", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                        process = new Process { StartInfo = processInfo };
                    }
                    else
                    {
                        var processInfo = new ProcessStartInfo { FileName = executable, Arguments = arguments ?? "", UseShellExecute = true, CreateNoWindow = false, WindowStyle = ParseWindowStyle(windowStyle) };
                        process = Process.Start(processInfo);
                    }
                }

                if (process == null) throw new Exception("Failed to create process");

                if (shellExecute && string.IsNullOrEmpty(asUser) && !elevated && string.IsNullOrEmpty(desktop))
                {
                    var outputBuilder = new StringBuilder();
                    var errorBuilder = new StringBuilder();
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    await WaitForProcessExit(process, timeoutMs, executable);
                    return process.ExitCode != 0 ? $"Exit code: {process.ExitCode}\n{errorBuilder}\n{outputBuilder}" : outputBuilder.ToString().Trim();
                }
                else
                {
                    int pid = createdProcessId ?? process.Id;
                    if (waitForExit) { await WaitForProcessExit(process, timeoutMs, executable); try { return $"Process exited with code {process.ExitCode}"; } catch { return $"Process exited (PID: {pid})"; } }
                    return $"Process started (PID: {pid})";
                }
            }
            finally
            {
                if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
                if (linkedToken != IntPtr.Zero) CloseHandle(linkedToken);
                if (userToken != IntPtr.Zero) CloseHandle(userToken);
            }
        }
        catch (Exception ex) { throw new Exception($"Failed to start application '{executable}': {ex.Message}"); }
    }

    public async Task<Process> StartProcessForStreaming(string executable, string arguments, string? asUser = null, string? desktop = null)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrEmpty(asUser))
            {
                uint sessionId = GetUserSessionId(asUser);
                EnablePrivilege(NativeMethods.SE_TCB_NAME);
                if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken)) userToken = TryStealUserToken(sessionId);
                if (userToken == IntPtr.Zero) throw new Exception($"Failed to get user token for session {sessionId}.");
                if (!DuplicateTokenEx(userToken, TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY, IntPtr.Zero, SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, TOKEN_TYPE.TokenPrimary, out primaryToken))
                    throw new Exception($"Failed to duplicate token. Error: {Marshal.GetLastWin32Error()}");
                return CreateProcessInternal(primaryToken, executable, arguments, out _, desktop ?? "winsta0\\default");
            }
            else if (!string.IsNullOrEmpty(desktop))
            {
                return CreateProcessInternal(null, executable, arguments, out _, desktop);
            }
            else
            {
                var startInfo = new ProcessStartInfo { FileName = executable, Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                var proc = new Process { StartInfo = startInfo };
                proc.Start();
                return proc;
            }
        }
        finally
        {
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    public string ListProcesses(int timeoutMs = 30000)
    {
        var startTime = DateTime.UtcNow;
        var result = new StringBuilder();
        foreach (var process in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs) throw new TimeoutException();
            try
            {
                string cmd = "";
                try { using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}"); foreach (ManagementObject obj in searcher.Get()) { cmd = obj["CommandLine"]?.ToString() ?? ""; break; } }
                catch { try { cmd = process.MainModule?.FileName ?? ""; } catch { cmd = "N/A"; } }
                result.AppendLine($"{process.Id}\t{process.ProcessName}\t{cmd}");
            }
            catch { continue; }
        }
        return result.ToString().Trim();
    }

    public string KillProcess(List<string>? names = null, List<int>? ids = null)
    {
        var killed = new List<string>();
        var failed = new List<string>();
        var targets = new List<Process>();
        if (names != null) foreach (var n in names) targets.AddRange(Process.GetProcessesByName(n.Replace(".exe", "")));
        if (ids != null) foreach (var id in ids) try { targets.Add(Process.GetProcessById(id)); } catch { }
        foreach (var p in targets.DistinctBy(p => p.Id))
        {
            try { p.Kill(); killed.Add($"{p.ProcessName} ({p.Id})"); }
            catch (Exception ex) { failed.Add($"{p.ProcessName} ({p.Id}): {ex.Message}"); }
        }
        var sb = new StringBuilder();
        if (killed.Any()) sb.AppendLine($"Killed: {string.Join(", ", killed)}");
        if (failed.Any()) sb.AppendLine($"Failed: {string.Join(", ", failed)}");
        return sb.ToString().Trim();
    }
}
