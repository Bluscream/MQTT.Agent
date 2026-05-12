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

        public static bool IsMoreStatesEnabled { get; } = Config.GetBool("more-states");
        public static bool IsTrayMode { get; } = Config.GetBool("tray");
        public static bool IsServiceMode { get; } = Config.GetBool("service");
        public static bool IsInstall { get; } = Config.GetBool("install");
        public static bool IsUninstall { get; } = Config.GetBool("uninstall");
        
        public static bool IsScreenshotHelper { get; } = Config.GetBool("screenshot-helper");
        public static bool IsMessageBoxHelper { get; } = Config.GetBool("messagebox");
        public static bool IsBannerHelper { get; } = Config.GetBool("banner");
        public static bool IsAnyHelper { get; } = IsScreenshotHelper || IsMessageBoxHelper || IsBannerHelper;

        public static bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);
        
        public static string? GetArgValue(string arg) => Config.GetArgValue(arg);

        public static string GetEnvOrConfig(IConfiguration config, string key, string? defaultValue = null) => Config.Get(key) is string s && !string.IsNullOrEmpty(s) ? s : defaultValue ?? string.Empty;

        private static bool HasArg(string arg) => Config.HasArg(arg);
    }
}
