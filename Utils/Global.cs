using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace MqttAgent.Utils
{
    public static class Global
    {
        public static string MachineName { get; } = Environment.MachineName;
        public static string SafeMachineName { get; } = Environment.MachineName.ToSafeMachineName();
        public static string UniqueId => SafeMachineName;

        public static class Args
        {
            public const string Tray = "--tray";
            public const string Service = "--service";
            public const string MoreStates = "--more-states";
            public const string Install = "--install";
            public const string Uninstall = "--uninstall";
            public const string ScreenshotHelper = "--screenshot-helper";
            public const string MessageBox = "--messagebox";
            public const string Banner = "--banner";
            public const string EntityState = "--entity-state";
            public const string EntityAttributes = "--entity-attributes";
            public const string Token = "-token";
        }

        private static readonly string _commandLine = Environment.CommandLine.ToLowerInvariant();
        private static readonly string[] _args = Environment.GetCommandLineArgs().Select(a => a.ToLowerInvariant()).ToArray();

        public static bool IsMoreStatesEnabled { get; } = HasArg(Args.MoreStates) || HasArg("/more-states");
        public static bool IsTrayMode { get; } = HasArg("-tray") || HasArg(Args.Tray) || HasArg("/tray");
        public static bool IsServiceMode { get; } = HasArg("-service") || HasArg(Args.Service) || HasArg("/service");
        public static bool IsInstall { get; } = HasArg(Args.Install) || HasArg("/install");
        public static bool IsUninstall { get; } = HasArg(Args.Uninstall) || HasArg("/uninstall");
        
        public static bool IsScreenshotHelper { get; } = HasArg(Args.ScreenshotHelper);
        public static bool IsMessageBoxHelper { get; } = HasArg(Args.MessageBox);
        public static bool IsBannerHelper { get; } = HasArg(Args.Banner);
        public static bool IsAnyHelper { get; } = IsScreenshotHelper || IsMessageBoxHelper || IsBannerHelper;

        public static bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);
        
        public static string? GetArgValue(string arg)
        {
            var index = Array.IndexOf(_args, arg.ToLowerInvariant());
            if (index >= 0 && index < _args.Length - 1) return _args[index + 1];
            
            var prefixed = _args.FirstOrDefault(a => a.StartsWith($"{arg.ToLowerInvariant()}:"));
            return prefixed?.Split(':', 2).LastOrDefault();
        }

        public static string GetEnvOrConfig(IConfiguration config, string key, string? defaultValue = null)
        {
            var envKey = key.ToUpperInvariant().Replace(":", "_");
            return config[envKey] ?? config[$"MqttAgent:{key}"] ?? config[key.ToLowerInvariant()] ?? defaultValue ?? string.Empty;
        }

        private static bool HasArg(string arg) => _args.Contains(arg.ToLowerInvariant()) || _commandLine.Contains(arg.ToLowerInvariant());
    }
}
