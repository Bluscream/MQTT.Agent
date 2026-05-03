using System.Management;
using System.Text;

namespace MqttAgent.Services;

public class ServiceService
{
    public string ListServices()
    {
        try
        {
            var result = new StringBuilder();
            result.AppendLine("Name\tDisplayName\tStatus\tStartType");
            result.AppendLine("----\t-----------\t------\t---------");
            
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Service");
            var services = searcher.Get()
                .Cast<ManagementObject>()
                .OrderBy(s => s["Name"]?.ToString() ?? "")
                .ToList();
            
            foreach (var service in services)
            {
                try
                {
                    var name = service["Name"]?.ToString() ?? "Unknown";
                    var displayName = service["DisplayName"]?.ToString() ?? "";
                    var state = service["State"]?.ToString() ?? "Unknown";
                    var startMode = service["StartMode"]?.ToString() ?? "Unknown";
                    
                    result.AppendLine($"{name}\t{displayName}\t{state}\t{startMode}");
                }
                catch
                {
                    // Skip services we can't access
                    continue;
                }
            }
            
            return result.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to list services: {ex.Message}");
        }
    }

    private const int ServiceOperationTimeoutSeconds = 30;
    private const int ServiceStateCheckIntervalMs = 500;

    public string StartService(string serviceName)
    {
        return ControlService(serviceName, "StartService", "Running", "Start Pending", 
            "start", "started", "starting");
    }

    public string StopService(string serviceName)
    {
        return ControlService(serviceName, "StopService", "Stopped", "Stop Pending", 
            "stop", "stopped", "stopping", checkAcceptStop: true);
    }

    private string ControlService(string serviceName, string methodName, string targetState, 
        string pendingState, string actionVerb, string actionPast, string actionPresent, bool checkAcceptStop = false)
    {
        try
        {
            var service = GetService(serviceName);
            var state = service["State"]?.ToString() ?? "";

            if (state == targetState)
            {
                return $"Service '{serviceName}' is already {actionPast}";
            }

            if (state == pendingState)
            {
                return $"Service '{serviceName}' is already {actionPresent}";
            }

            if (checkAcceptStop)
            {
                var acceptStop = service["AcceptStop"]?.ToString() ?? "False";
                if (acceptStop != "True")
                {
                    throw new Exception($"Service '{serviceName}' cannot be stopped");
                }
            }

            var result = service.InvokeMethod(methodName, null);
            if (result != null && (uint)result != 0)
            {
                throw new Exception($"Failed to {actionVerb} service. Error code: {(uint)result}");
            }

            return WaitForServiceState(service, serviceName, targetState, pendingState, actionPast, actionPresent);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to {actionVerb} service '{serviceName}': {ex.Message}");
        }
    }

    private ManagementObject GetService(string serviceName)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT * FROM Win32_Service WHERE Name = '{serviceName.Replace("'", "''")}'");
        var services = searcher.Get().Cast<ManagementObject>().ToList();

        if (services.Count == 0)
        {
            throw new Exception($"Service '{serviceName}' not found");
        }

        return services[0];
    }

    private string WaitForServiceState(ManagementObject service, string serviceName, 
        string targetState, string pendingState, string actionPast, string actionPresent)
    {
        var timeout = DateTime.UtcNow.AddSeconds(ServiceOperationTimeoutSeconds);
        while (DateTime.UtcNow < timeout)
        {
            service.Get();
            var state = service["State"]?.ToString() ?? "";
            
            if (state == targetState)
            {
                return $"Service '{serviceName}' {actionPast} successfully";
            }
            
            if (state != pendingState)
            {
                throw new Exception($"Service '{serviceName}' failed to {actionPast}. Current state: {state}");
            }
            
            Thread.Sleep(ServiceStateCheckIntervalMs);
        }

        throw new TimeoutException($"Service '{serviceName}' did not {actionPast} within {ServiceOperationTimeoutSeconds} seconds");
    }
}
