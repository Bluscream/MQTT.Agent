using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace MqttAgent.Utils;

public static class Config
{
    private static IConfiguration? _configuration;
    private static readonly string[] _args = Environment.GetCommandLineArgs();
    private static readonly string _commandLine = Environment.CommandLine.ToLowerInvariant();

    public static void Initialize(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Gets a configuration value by checking command line args, environment variables, and configuration providers.
    /// Supports multiple keys (aliases) and returns the first found value.
    /// </summary>
    public static string Get(params string[] keys)
    {
        foreach (var key in keys)
        {
            var val = GetInternal(key);
            if (!string.IsNullOrEmpty(val)) return val;
        }
        return string.Empty;
    }

    /// <summary>
    /// Gets a boolean configuration value. Returns true if any of the keys exist as a command line flag 
    /// or have a value that evaluates to true.
    /// </summary>
    public static bool GetBool(params string[] keys)
    {
        foreach (var key in keys)
        {
            var lowerK = key.TrimStart('-').TrimStart('/').ToLowerInvariant();
            
            // Check for existence as a flag first (e.g. --tray, /tray, tray)
            if (HasArg($"--{lowerK}") || HasArg($"/{lowerK}") || HasArg(key)) return true;

            // Then check for explicit values (true/false, 1/0, etc)
            var val = GetInternal(key);
            if (!string.IsNullOrEmpty(val) && val.ToBoolean()) return true;
        }
        return false;
    }

    private static string GetInternal(string key)
    {
        var cleanKey = key.TrimStart('-').TrimStart('/');

        // 1. Args: --key or /key
        var argValue = _args.GetArgValue($"--{cleanKey}") ?? _args.GetArgValue($"/{cleanKey}");
        if (!string.IsNullOrEmpty(argValue)) return argValue;

        // 2. Env Vars: KEY_NAME (replace - with _)
        var envVal = Environment.GetEnvironmentVariable(cleanKey.ToEnvKey()) ?? 
                     Environment.GetEnvironmentVariable(cleanKey.ToEnvKey("MQTTAGENT_"));
        if (!string.IsNullOrEmpty(envVal)) return envVal;

        // 3. Configuration (appsettings.json / MqttAgent.json)
        if (_configuration != null)
        {
            var cfgVal = _configuration.GetWithAliases(cleanKey);
            if (!string.IsNullOrEmpty(cfgVal)) return cfgVal;
        }

        return string.Empty;
    }

    public static bool HasArg(string arg) 
    {
        var lowerArg = arg.ToLowerInvariant();
        return _args.Any(a => a.ToLowerInvariant() == lowerArg || a.ToLowerInvariant() == $"--{lowerArg.TrimStart('-')}" || a.ToLowerInvariant() == $"/{lowerArg.TrimStart('/')}");
    }

    public static string? GetArgValue(string arg) => _args.GetArgValue(arg);
}
