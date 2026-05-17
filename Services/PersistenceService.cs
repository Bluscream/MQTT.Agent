using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Microsoft.Extensions.Logging;
using System.Linq;
using MqttAgent.Utils;

namespace MqttAgent.Services
{
    public interface IPersistenceService
    {
        void EnsureServiceSafeBoot();
        void EnsureMoreStatesTriggers();
        void EnsureFirewallRule(int port);
        void Uninstall();
    }

    public class PersistenceService : IPersistenceService
    {
        private readonly ILogger<PersistenceService> _logger;
        private readonly string _exePath;
        private const string ServiceName = "MqttAgent";
        private const string DisplayName = "MQTT.Agent PC Monitor";
        private const string FirewallRuleName = "MQTT Agent";

        public PersistenceService(ILogger<PersistenceService> logger)
        {
            _logger = logger;
            _exePath = Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Could not determine exe path.");
        }

        public void EnsureServiceSafeBoot()
        {
            EnsureServiceInstalled();
            RegisterSafeBoot("Minimal");
            RegisterSafeBoot("Network");
        }

        public void Uninstall()
        {
            try
            {
                _logger.LogInformation("Uninstalling service and cleaning up persistence...");
                ServiceHelper.UninstallService(ServiceName);
                
                using (TaskService ts = new TaskService())
                {
                    ts.RootFolder.DeleteTask(ServiceName + "_Logon", false);
                    var folder = ts.GetFolder(@"\mqtt\events");
                    if (folder != null)
                    {
                        foreach (var task in folder.Tasks) folder.DeleteTask(task.Name);
                        ts.RootFolder.DeleteFolder(@"\mqtt\events", false);
                    }
                }

                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", true)?.DeleteSubKeyTree(ServiceName, false);
                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Network", true)?.DeleteSubKeyTree(ServiceName, false);
                Registry.CurrentUser.OpenSubKey(@"Environment", true)?.DeleteValue("UserInitMprLogonScript", false);
                Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)?.DeleteValue(ServiceName, false);
                
                var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var batchPath = Path.Combine(startupFolder, ServiceName + "_Startup.bat");
                if (File.Exists(batchPath)) File.Delete(batchPath);

                DeleteFirewallRule();

                _logger.LogInformation("Persistence cleanup complete.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed during uninstall");
            }
        }

        public void EnsureFirewallRule(int port)
        {
            _logger.LogInformation("Ensuring firewall rule for port {Port}...", port);
            try
            {
                var typePolicy = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (typePolicy == null) throw new InvalidOperationException("Could not get firewall policy type.");
                dynamic firewallPolicy = Activator.CreateInstance(typePolicy) ?? throw new InvalidOperationException("Could not create firewall policy instance.");
                
                DeleteFirewallRule(firewallPolicy);

                var typeRule = Type.GetTypeFromProgID("HNetCfg.FWRule");
                if (typeRule == null) throw new InvalidOperationException("Could not get firewall rule type.");
                dynamic rule = Activator.CreateInstance(typeRule) ?? throw new InvalidOperationException("Could not create firewall rule instance.");

                rule.Name = FirewallRuleName;
                rule.Description = $"Inbound rule for {DisplayName}";
                rule.Action = 1; // NET_FW_ACTION_ALLOW
                rule.Direction = 1; // NET_FW_RULE_DIR_IN
                rule.Enabled = true;
                rule.InterfaceTypes = "All";
                rule.Protocol = 6; // TCP
                rule.LocalPorts = port.ToString();
                rule.ApplicationName = _exePath;
                rule.Profiles = 0x7FFFFFFF; // All profiles (Domain, Private, Public)

                firewallPolicy.Rules.Add(rule);
                _logger.LogInformation("Firewall rule '{FirewallRuleName}' ensured for port {Port} (via COM).", FirewallRuleName, port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure firewall rule via COM API");
                _logger.LogWarning("Falling back to netsh for firewall rule...");
                RunNetshFallback(port);
            }
        }

        private void DeleteFirewallRule(dynamic? policy = null)
        {
            try
            {
                if (policy == null)
                {
                    var typePolicy = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                    if (typePolicy != null) policy = Activator.CreateInstance(typePolicy);
                }

                if (policy != null)
                {
                    policy.Rules.Remove(FirewallRuleName);
                }
            }
            catch { /* Ignore if rule doesn't exist */ }
        }

        private void RunNetshFallback(int port)
        {
            try
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{FirewallRuleName}\"");
                RunNetsh($"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow program=\"{_exePath}\" localport={port} protocol=tcp profile=any");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Firewall fallback also failed");
            }
        }

        private void RunNetsh(string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            process.WaitForExit();
        }

        private void EnsureServiceInstalled()
        {
            _logger.LogInformation("Checking if service '{ServiceName}' is installed...", ServiceName);

            try
            {
                // Build binPath including current operational flags
                string binPath = $"\"{_exePath}\" {Global.Args.Service}";
                if (Global.IsStartTrayEnabled) binPath += $" {Global.Args.StartTray}";

                if (!ServiceHelper.IsServiceInstalled(ServiceName))
                {
                    _logger.LogInformation("Service not found. Installing with binPath: {BinPath}", binPath);
                    ServiceHelper.InstallService(ServiceName, DisplayName, binPath);
                    _logger.LogInformation("Service installed successfully.");
                }
                else
                {
                    _logger.LogInformation("Service already installed. Updating configuration natively...");
                    ServiceHelper.UpdateServiceBinaryPath(ServiceName, binPath);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to manage service: {Message}", ex.Message);
            }
        }

        private void RegisterSafeBoot(string mode)
        {
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\SafeBoot\{mode}", true);
                if (baseKey != null)
                {
                    using var svcKey = baseKey.CreateSubKey(ServiceName);
                    svcKey?.SetValue(null, "Service");
                    _logger.LogInformation("Service registered for Safe Mode ({Mode}).", mode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to register service for Safe Mode ({Mode}): {Message}", mode, ex.Message);
            }
        }

        public void EnsureMoreStatesTriggers()
        {
            _logger.LogInformation("Ensuring fine-grained logon triggers...");

            SetupScheduledTask();
            EnsureEventTasks();
            SetupLogonScript();
            SetupRunKey();
            SetupStartupFolder();
            SetupRunOnceSafeMode();
        }

        private void EnsureEventTasks()
        {
            try
            {
                using TaskService ts = new TaskService();
                var folderPath = @"\mqtt\events";
                
                var parts = folderPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                TaskFolder currentFolder = ts.RootFolder;
                foreach (var part in parts)
                {
                    TaskFolder? next = null;
                    try { next = currentFolder.SubFolders.FirstOrDefault(f => f.Name.Equals(part, StringComparison.OrdinalIgnoreCase)); } catch { }
                    if (next == null)
                    {
                        currentFolder = currentFolder.CreateFolder(part);
                    }
                    else
                    {
                        currentFolder = next;
                    }
                }
                
                if (currentFolder == null)
                {
                    _logger.LogError("Failed to get or create folder: {FolderPath}", folderPath);
                    return;
                }

                _logger.LogInformation("Ensured event task folder: {Path}", currentFolder.Path);

                var tasksToCreate = new[]
                {
                    (Name: "BootTrigger", State: "Booting", Trigger: (Trigger)new BootTrigger()),
                    (Name: "LogonTrigger", State: "Logged In (Logon Trigger)", Trigger: (Trigger)new LogonTrigger()),
                    (Name: "IdleTrigger", State: "Idle (Task Scheduler)", Trigger: (Trigger)new IdleTrigger()),
                    (Name: "SessionLock", State: "Locked", Trigger: (Trigger)new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.SessionLock }),
                    (Name: "SessionUnlock", State: "Unlocked", Trigger: (Trigger)new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.SessionUnlock }),
                    (Name: "ConsoleConnect", State: "Console Connected", Trigger: (Trigger)new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.ConsoleConnect }),
                    (Name: "ConsoleDisconnect", State: "Console Disconnected", Trigger: (Trigger)new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.ConsoleDisconnect }),
                    (Name: "RemoteConnect", State: "Remote Connected", Trigger: (Trigger)new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.RemoteConnect }),
                    (Name: "RemoteDisconnect", State: "Remote Disconnected", Trigger: (Trigger)new SessionStateChangeTrigger { StateChange = TaskSessionStateChangeType.RemoteDisconnect }),
                    (Name: "WindowsUpdateFinished", State: "Update Finished", Trigger: (Trigger)new EventTrigger("System", "Microsoft-Windows-WindowsUpdateClient", 19))
                };

                foreach (var taskInfo in tasksToCreate)
                {
                    TaskDefinition td = ts.NewTask();
                    td.RegistrationInfo.Description = $"{ServiceName} {taskInfo.Name} (Managed)";
                    td.Triggers.Add(taskInfo.Trigger);
                    td.Actions.Add(new ExecAction(_exePath, $"{Global.Args.EntityState} \"{taskInfo.State}\" {Global.Args.EntityAttributes} \"{{\\\"source\\\":\\\"Task Scheduler\\\"}}\""));
                    
                    td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                    td.Settings.DisallowStartIfOnBatteries = false;
                    td.Settings.StopIfGoingOnBatteries = false;
                    td.Settings.StartWhenAvailable = true;

                    currentFolder.RegisterTaskDefinition(taskInfo.Name, td, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);
                    _logger.LogInformation("Ensured event task: {Path}", Path.Combine(folderPath, taskInfo.Name));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to setup event tasks: {Message}", ex.Message);
            }
        }

        private void SetupRunOnceSafeMode()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", true);
                if (key != null)
                {
                    var cmd = $"\"{_exePath}\" {Global.Args.EntityState} \"Safe Mode Startup (RunOnce)\"";
                    key.SetValue("*" + ServiceName, cmd);
                    _logger.LogInformation("Registry RunOnce Safe Mode asterisk hook ensured.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to setup RunOnce Safe Mode hook: {Message}", ex.Message);
            }
        }

        private void SetupScheduledTask()
        {
            try
            {
                using TaskService ts = new TaskService();
                TaskDefinition td = ts.NewTask();
                td.RegistrationInfo.Description = $"{ServiceName} Logon Trigger (Scheduled Task)";
                td.Triggers.Add(new LogonTrigger());
                td.Actions.Add(new ExecAction(_exePath, $"{Global.Args.EntityState} \"Logged In (Scheduled Task)\""));
                
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                ts.RootFolder.RegisterTaskDefinition(ServiceName + "_Logon", td);
                _logger.LogInformation("Scheduled Task '{ServiceName}_Logon' ensured (One-off report).", ServiceName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to setup Scheduled Task: {Message}", ex.Message);
            }
        }

        private void SetupLogonScript()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Environment", true);
                if (key != null)
                {
                    var cmd = $"\"{_exePath}\" {Global.Args.Tray}";
                    key.SetValue("UserInitMprLogonScript", cmd);
                    _logger.LogInformation("Registry Logon Script ensured (Tray mode).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to setup Logon Script: {Message}", ex.Message);
            }
        }

        private void SetupRunKey()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    var cmd = $"\"{_exePath}\" {Global.Args.Tray}";
                    key.SetValue(ServiceName, cmd);
                    _logger.LogInformation("Registry Run key ensured (Tray mode).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to setup Run key: {Message}", ex.Message);
            }
        }

        private void SetupStartupFolder()
        {
            try
            {
                var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var batchPath = Path.Combine(startupFolder, ServiceName + "_Startup.bat");
                var content = $"@echo off\nstart \"\" \"{_exePath}\" {Global.Args.Tray}";
                
                File.WriteAllText(batchPath, content);
                _logger.LogInformation("Startup folder batch file ensured (Tray mode).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to setup Startup folder batch: {Message}", ex.Message);
            }
        }
    }
}
