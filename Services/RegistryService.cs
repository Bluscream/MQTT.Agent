using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MqttAgent.Services;

public class RegistryService
{
    public string ReadRegistry(string keyPath, string? valueName = null, string hive = "HKEY_CURRENT_USER")
    {
        try
        {
            var baseKey = GetRegistryHive(hive);
            if (baseKey == null)
            {
                throw new Exception($"Invalid registry hive: {hive}");
            }

            using (baseKey)
            {
                var cleanPath = CleanRegistryPath(keyPath, hive);
                using var key = baseKey.OpenSubKey(cleanPath);
                if (key == null)
                {
                    throw new Exception($"Registry key not found: {hive}\\{cleanPath}");
                }

                if (string.IsNullOrEmpty(valueName))
                {
                    // Return all values in the key
                    var result = new StringBuilder();
                    result.AppendLine($"Registry Key: {hive}\\{cleanPath}");
                    result.AppendLine("Values:");
                    
                    foreach (var name in key.GetValueNames())
                    {
                        var value = key.GetValue(name);
                        var valueType = key.GetValueKind(name);
                        result.AppendLine($"  {name} ({valueType}) = {value}");
                    }
                    
                    // Also list subkeys
                    var subKeys = key.GetSubKeyNames();
                    if (subKeys.Length > 0)
                    {
                        result.AppendLine("\nSubkeys:");
                        foreach (var subKey in subKeys)
                        {
                            result.AppendLine($"  {subKey}");
                        }
                    }
                    
                    return result.ToString().TrimEnd();
                }
                else
                {
                    // Return specific value
                    var value = key.GetValue(valueName);
                    if (value == null)
                    {
                        throw new Exception($"Registry value not found: {hive}\\{cleanPath}\\{valueName}");
                    }
                    
                    var valueType = key.GetValueKind(valueName);
                    return $"{valueName} ({valueType}) = {value}";
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to read registry: {ex.Message}");
        }
    }

    public string WriteRegistry(string keyPath, string valueName, string value, string valueType = "String", string hive = "HKEY_CURRENT_USER")
    {
        try
        {
            RegistryKey? baseKey = GetRegistryHive(hive);
            if (baseKey == null)
            {
                throw new Exception($"Invalid registry hive: {hive}");
            }

            using (baseKey)
            {
                var cleanPath = CleanRegistryPath(keyPath, hive);
                using var key = baseKey.CreateSubKey(cleanPath, true);
                if (key == null)
                {
                    throw new Exception($"Failed to create/open registry key: {hive}\\{cleanPath}");
                }

                // Convert value based on type
                RegistryValueKind kind = valueType.ToLower() switch
                {
                    "string" or "reg_sz" => RegistryValueKind.String,
                    "dword" or "reg_dword" => RegistryValueKind.DWord,
                    "qword" or "reg_qword" => RegistryValueKind.QWord,
                    "binary" or "reg_binary" => RegistryValueKind.Binary,
                    "multistring" or "reg_multi_sz" => RegistryValueKind.MultiString,
                    "expandstring" or "reg_expand_sz" => RegistryValueKind.ExpandString,
                    _ => RegistryValueKind.String
                };

                object? convertedValue = kind switch
                {
                    RegistryValueKind.DWord => int.Parse(value),
                    RegistryValueKind.QWord => long.Parse(value),
                    RegistryValueKind.Binary => Convert.FromHexString(value.Replace(" ", "").Replace("-", "")),
                    RegistryValueKind.MultiString => value.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries),
                    _ => value
                };

                key.SetValue(valueName, convertedValue, kind);
                return $"Successfully wrote {valueName} = {value} ({kind}) to {hive}\\{cleanPath}";
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to write registry: {ex.Message}");
        }
    }

    public string SearchRegistry(string query, string? path = null, bool searchKeys = true, bool searchValues = true, bool searchData = true, string hive = "HKEY_CURRENT_USER")
    {
        try
        {
            var baseKey = GetRegistryHive(hive);
            if (baseKey == null)
            {
                throw new Exception($"Invalid registry hive: {hive}");
            }

            // Convert glob pattern to regex
            var regexPattern = GlobToRegex(query);
            var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

            // Determine starting path
            var cleanPath = !string.IsNullOrEmpty(path) ? CleanRegistryPath(path, hive) : null;

            var results = new ConcurrentBag<RegistrySearchResult>();

            // Use parallel processing for fast search
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            // Search starting from the specified path or root
            if (cleanPath != null)
            {
                using (baseKey)
                {
                    using var startKey = baseKey.OpenSubKey(cleanPath);
                    if (startKey != null)
                    {
                        SearchRegistryRecursive(startKey, hive + "\\" + cleanPath, regex, searchKeys, searchValues, searchData, results, parallelOptions);
                    }
                }
            }
            else
            {
                // Search from root of the hive
                using (baseKey)
                {
                    SearchRegistryRecursive(baseKey, hive, regex, searchKeys, searchValues, searchData, results, parallelOptions);
                }
            }

            // Format results
            if (results.IsEmpty)
            {
                return "No matches found.";
            }

            var output = new StringBuilder();
            output.AppendLine($"Found {results.Count} match(es):\n");

            foreach (var result in results.OrderBy(r => r.Path))
            {
                output.AppendLine($"Path: {result.Path}");
                if (!string.IsNullOrEmpty(result.MatchType))
                {
                    output.AppendLine($"  Match Type: {result.MatchType}");
                }
                if (!string.IsNullOrEmpty(result.Details))
                {
                    output.AppendLine($"  {result.Details}");
                }
                output.AppendLine();
            }

            return output.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to search registry: {ex.Message}");
        }
    }

    private void SearchRegistryRecursive(RegistryKey key, string currentPath, Regex regex, bool searchKeys, bool searchValues, bool searchData, ConcurrentBag<RegistrySearchResult> results, ParallelOptions parallelOptions)
    {
        try
        {
            // Search in key names (current key)
            if (searchKeys && regex.IsMatch(key.Name))
            {
                results.Add(new RegistrySearchResult
                {
                    Path = currentPath,
                    MatchType = "Key Name",
                    Details = $"Key name matches pattern"
                });
            }

            // Search in value names and data
            if (searchValues || searchData)
            {
                try
                {
                    var valueNames = key.GetValueNames();
                    foreach (var valueName in valueNames)
                    {
                        // Search in value name
                        if (searchValues && regex.IsMatch(valueName))
                        {
                            results.Add(new RegistrySearchResult
                            {
                                Path = currentPath,
                                MatchType = "Value Name",
                                Details = $"Value: {valueName}"
                            });
                        }

                        // Search in value data
                        if (searchData)
                        {
                            try
                            {
                                var value = key.GetValue(valueName);
                                if (value != null)
                                {
                                    var valueStr = ConvertValueToString(value, key.GetValueKind(valueName));
                                    if (regex.IsMatch(valueStr))
                                    {
                                        results.Add(new RegistrySearchResult
                                        {
                                            Path = currentPath,
                                            MatchType = "Value Data",
                                            Details = $"Value: {valueName} = {TruncateString(valueStr, 100)}"
                                        });
                                    }
                                }
                            }
                            catch
                            {
                                // Skip values that can't be read
                            }
                        }
                    }
                }
                catch
                {
                    // Skip keys that can't be read
                }
            }

            // Recursively search subkeys in parallel
            try
            {
                var subKeyNames = key.GetSubKeyNames();
                Parallel.ForEach(subKeyNames, parallelOptions, subKeyName =>
                {
                    try
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey != null)
                        {
                            var subKeyPath = string.IsNullOrEmpty(currentPath) ? subKeyName : $"{currentPath}\\{subKeyName}";
                            SearchRegistryRecursive(subKey, subKeyPath, regex, searchKeys, searchValues, searchData, results, parallelOptions);
                        }
                    }
                    catch
                    {
                        // Skip subkeys that can't be accessed
                    }
                });
            }
            catch
            {
                // Skip if subkeys can't be enumerated
            }
        }
        catch
        {
            // Skip keys that cause errors
        }
    }

    private string GlobToRegex(string glob)
    {
        // Convert glob pattern to regex
        // * matches any sequence of characters
        // ? matches any single character
        // Escape special regex characters
        var regex = new StringBuilder();
        regex.Append("^");
        
        foreach (var c in glob)
        {
            switch (c)
            {
                case '*':
                    regex.Append(".*");
                    break;
                case '?':
                    regex.Append(".");
                    break;
                case '.':
                case '+':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '^':
                case '$':
                case '|':
                case '\\':
                    regex.Append("\\").Append(c);
                    break;
                default:
                    regex.Append(c);
                    break;
            }
        }
        
        regex.Append("$");
        return regex.ToString();
    }

    private string ConvertValueToString(object? value, RegistryValueKind kind)
    {
        if (value == null) return string.Empty;

        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => value.ToString() ?? string.Empty,
            RegistryValueKind.MultiString => string.Join(" ", ((string[])value)),
            RegistryValueKind.DWord => value.ToString() ?? string.Empty,
            RegistryValueKind.QWord => value.ToString() ?? string.Empty,
            RegistryValueKind.Binary => BitConverter.ToString((byte[])value).Replace("-", ""),
            _ => value.ToString() ?? string.Empty
        };
    }

    private string TruncateString(string str, int maxLength)
    {
        if (str.Length <= maxLength) return str;
        return str.Substring(0, maxLength) + "...";
    }

    private static string CleanRegistryPath(string keyPath, string hive)
    {
        if (keyPath.StartsWith(hive + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return keyPath.Substring(hive.Length + 1);
        }
        if (keyPath.StartsWith(hive, StringComparison.OrdinalIgnoreCase))
        {
            return keyPath.Substring(hive.Length);
        }
        return keyPath;
    }

    private RegistryKey? GetRegistryHive(string hive)
    {
        return hive.ToUpper() switch
        {
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CLASSES_ROOT" or "HKCR" => Registry.ClassesRoot,
            "HKEY_USERS" or "HKU" => Registry.Users,
            "HKEY_CURRENT_CONFIG" or "HKCC" => Registry.CurrentConfig,
            _ => null
        };
    }

    private class RegistrySearchResult
    {
        public string Path { get; set; } = string.Empty;
        public string MatchType { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }
}
