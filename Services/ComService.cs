using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MqttAgent.Services;

public class ComService
{
    private static readonly string[] CommonComObjects = 
    {
        "Shell.Application",
        "Scripting.FileSystemObject",
        "WScript.Shell"
    };

    public List<string> ListComObjects()
    {
        var comObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            using var classesRoot = Registry.ClassesRoot;
            using var clsidKey = classesRoot.OpenSubKey("CLSID");
            
            if (clsidKey != null)
            {
                foreach (var clsid in clsidKey.GetSubKeyNames())
                {
                    try
                    {
                        using var clsidSubKey = clsidKey.OpenSubKey(clsid);
                        if (clsidSubKey == null) continue;

                        AddProgIdFromSubKey(clsidSubKey, "ProgID", comObjects);
                        AddProgIdFromSubKey(clsidSubKey, "VersionIndependentProgID", comObjects);
                    }
                    catch
                    {
                        // Skip invalid entries
                    }
                }
            }
        }
        catch
        {
            // Fallback handled below
        }
        
        // Fallback to common COM objects if enumeration failed or returned empty
        if (comObjects.Count == 0)
        {
            foreach (var obj in CommonComObjects)
            {
                comObjects.Add(obj);
            }
        }
        
        return comObjects.OrderBy(x => x).ToList();
    }

    private static void AddProgIdFromSubKey(RegistryKey clsidSubKey, string subKeyName, HashSet<string> comObjects)
    {
        using var progIdKey = clsidSubKey.OpenSubKey(subKeyName);
        var progId = progIdKey?.GetValue(null)?.ToString();
        if (!string.IsNullOrEmpty(progId))
        {
            comObjects.Add(progId);
        }
    }

    public string QueryComObject(string? progId = null, string? clsid = null)
    {
        try
        {
            var result = new StringBuilder();
            
            if (!string.IsNullOrEmpty(progId))
            {
                // Query by ProgID
                var type = Type.GetTypeFromProgID(progId);
                if (type == null)
                {
                    throw new Exception($"COM object '{progId}' not found");
                }
                
                result.AppendLine($"ProgID: {progId}");
                result.AppendLine($"Type: {type.FullName}");
                result.AppendLine($"CLSID: {type.GUID}");
                result.AppendLine("\nMethods:");
                
                var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                foreach (var method in methods.OrderBy(m => m.Name))
                {
                    var parameters = method.GetParameters();
                    var paramList = string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    result.AppendLine($"  {method.ReturnType.Name} {method.Name}({paramList})");
                }
                
                result.AppendLine("\nProperties:");
                var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                foreach (var prop in properties.OrderBy(p => p.Name))
                {
                    result.AppendLine($"  {prop.PropertyType.Name} {prop.Name} {{ get; {(prop.CanWrite ? "set; " : "")}}}");
                }
            }
            else if (!string.IsNullOrEmpty(clsid))
            {
                // Query by CLSID
                var guid = Guid.Parse(clsid);
                var type = Type.GetTypeFromCLSID(guid);
                if (type == null)
                {
                    throw new Exception($"COM object with CLSID '{clsid}' not found");
                }
                
                result.AppendLine($"CLSID: {clsid}");
                result.AppendLine($"Type: {type.FullName}");
                
                // Try to find ProgID from registry
                using var classesRoot = Registry.ClassesRoot;
                using var clsidKey = classesRoot.OpenSubKey($"CLSID\\{{{clsid}}}");
                if (clsidKey != null)
                {
                    using var progIdKey = clsidKey.OpenSubKey("ProgID");
                    if (progIdKey != null)
                    {
                        var foundProgId = progIdKey.GetValue(null)?.ToString();
                        if (!string.IsNullOrEmpty(foundProgId))
                        {
                            result.AppendLine($"ProgID: {foundProgId}");
                        }
                    }
                }
            }
            else
            {
                // List all COM objects (return list)
                var comObjects = ListComObjects();
                result.AppendLine($"Found {comObjects.Count} COM objects:");
                foreach (var obj in comObjects.Take(100)) // Limit to first 100
                {
                    result.AppendLine($"  {obj}");
                }
                if (comObjects.Count > 100)
                {
                    result.AppendLine($"  ... and {comObjects.Count - 100} more");
                }
            }
            
            return result.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to query COM object: {ex.Message}");
        }
    }

    public string SendComMessage(string progId, string method, Dictionary<string, object>? parameters = null)
    {
        try
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type == null)
            {
                throw new Exception($"COM object '{progId}' not found");
            }
            
            var comObject = Activator.CreateInstance(type);
            if (comObject == null)
            {
                throw new Exception($"Failed to create instance of '{progId}'");
            }
            
            // Find the method
            var methodInfo = type.GetMethod(method);
            if (methodInfo == null)
            {
                throw new Exception($"Method '{method}' not found on COM object '{progId}'");
            }
            
            // Get method parameters
            var paramInfos = methodInfo.GetParameters();
            object? result;
            
            if (paramInfos.Length == 0)
            {
                // No parameters
                result = methodInfo.Invoke(comObject, null);
            }
            else if (parameters != null && parameters.Count > 0)
            {
                // Convert parameters to method signature
                var args = new object[paramInfos.Length];
                for (int i = 0; i < paramInfos.Length; i++)
                {
                    var paramInfo = paramInfos[i];
                    if (parameters.TryGetValue(paramInfo.Name ?? "", out var paramValue))
                    {
                        // Try to convert to the expected type
                        args[i] = Convert.ChangeType(paramValue, paramInfo.ParameterType)!;
                    }
                    else if (paramInfo.HasDefaultValue)
                    {
                        args[i] = paramInfo.DefaultValue ?? (paramInfo.ParameterType.IsValueType ? Activator.CreateInstance(paramInfo.ParameterType)! : null)!;
                    }
                    else
                    {
                        throw new Exception($"Missing required parameter '{paramInfo.Name}'");
                    }
                }
                result = methodInfo.Invoke(comObject, args);
            }
            else
            {
                // Use default values for all parameters
                var args = new object[paramInfos.Length];
                for (int i = 0; i < paramInfos.Length; i++)
                {
                    var paramInfo = paramInfos[i];
                    args[i] = paramInfo.HasDefaultValue 
                        ? (paramInfo.DefaultValue ?? (paramInfo.ParameterType.IsValueType ? Activator.CreateInstance(paramInfo.ParameterType)! : null)!)
                        : (paramInfo.ParameterType.IsValueType ? Activator.CreateInstance(paramInfo.ParameterType)! : null)!;
                }
                result = methodInfo.Invoke(comObject, args);
            }
            
            // Return result as string
            if (result == null)
            {
                return $"Method '{method}' executed successfully (returned null)";
            }
            
            return result.ToString() ?? $"Method '{method}' executed successfully";
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to send COM message: {ex.Message}");
        }
    }
}
