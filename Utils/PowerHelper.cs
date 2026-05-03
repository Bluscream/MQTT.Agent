using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace MqttAgent.Utils
{
    public static class PowerHelper
    {
        public static List<(string Name, string Guid)> GetPowerSchemes()
        {
            var schemes = new List<(string Name, string Guid)>();
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/list",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var regex = new Regex(@"GUID: ([\w-]+)\s+\((.+)\)");
                var matches = regex.Matches(output);

                foreach (Match match in matches)
                {
                    var guid = match.Groups[1].Value;
                    var name = match.Groups[2].Value.Trim();
                    if (name.EndsWith("*")) name = name.Substring(0, name.Length - 1).Trim();
                    schemes.Add((name, guid));
                }
            }
            catch { }
            return schemes;
        }

        public static string GetActiveScheme()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = "/getactivescheme",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var regex = new Regex(@"GUID: ([\w-]+)\s+\((.+)\)");
                var match = regex.Match(output);
                if (match.Success)
                {
                    return match.Groups[2].Value.Trim();
                }
            }
            catch { }
            return "Unknown";
        }

        public static bool SetActiveScheme(string schemeName)
        {
            var schemes = GetPowerSchemes();
            var target = schemes.FirstOrDefault(s => s.Name.Equals(schemeName, StringComparison.OrdinalIgnoreCase));
            
            if (string.IsNullOrEmpty(target.Guid)) return false;

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = $"/setactive {target.Guid}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
