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
        void Uninstall();
    }

    public class PersistenceService : IPersistenceService
    {
        private readonly ILogger<PersistenceService> _logger;
        private readonly string _exePath;
        private const string ServiceName = "MqttAgent";
        private const string DisplayName = "MQTT.Agent PC Monitor";

        public PersistenceService(ILogger<PersistenceService> logger)
        {
            _logger = logger;
            _exePath = Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Could not determine exe path.");
        }

        public void EnsureServiceSafeBoot()
        {
            InstallAndStartService();
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

                _logger.LogInformation("Persistence cleanup complete.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed during uninstall");
            }
        }

        private void InstallAndStartService()
        {
            _logger.LogInformation("Checking if service '{ServiceName}' is installed...", ServiceName);

            try
            {
                if (!ServiceHelper.IsServiceInstalled(ServiceName))
                {
                    _logger.LogInformation("Service not found. Installing...");
                    string binPath = $"\"{_exePath}\" {Global.Args.Service}";
                    ServiceHelper.InstallService(ServiceName, DisplayName, binPath);
                    _logger.LogInformation("Service installed successfully.");
                }
                else
                {
                    _logger.LogInformation("Service already installed.");
                }

                _logger.LogInformation("Ensuring service is started...");
                ServiceHelper.StartService(ServiceName);
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
