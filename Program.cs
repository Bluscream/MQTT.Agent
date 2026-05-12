using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MqttAgent.Services;
using MqttAgent.Utils;
using Serilog;
using System.Text.Json;

namespace MqttAgent;

public static class Program
{
    [STAThread]
    public static async Task Main(string[] args)
    {
        if (args.Contains("--screenshot-helper") || args.Contains("--messagebox") || args.Contains("--banner"))
        {
            SessionHelper.Run(args);
            return;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        // Configure Logging
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(baseDir, "logs", "mqtt-agent.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();

        // Extract args
        bool isTray = args.Contains("-tray") || args.Contains("--tray") || args.Contains("/tray");
        bool isService = args.Contains("-service") || args.Contains("--service") || args.Contains("/service");
        
        var token = builder.Configuration["MQTTAGENT_TOKEN"];
        if (string.IsNullOrEmpty(token)) token = builder.Configuration["MqttAgent:Token"];
        if (string.IsNullOrEmpty(token)) token = builder.Configuration["token"];
        if (string.IsNullOrEmpty(token)) token = args.FirstOrDefault(a => a.StartsWith("-token:"))?.Split(':')[1];
        if (string.IsNullOrEmpty(token)) token = args.SkipWhile(a => a != "-token").Skip(1).FirstOrDefault();

        if (string.IsNullOrEmpty(token))
        {
            Log.Fatal("CRITICAL ERROR: No MQTTAGENT_TOKEN provided. The application cannot start without an authentication token for security reasons. Please set the MQTTAGENT_TOKEN environment variable or provide it via appsettings.json.");
            throw new InvalidOperationException("MQTTAGENT_TOKEN is required.");
        }

        // Configure Kestrel
        var portStr = builder.Configuration["MQTTAGENT_PORT"];
        if (string.IsNullOrEmpty(portStr)) portStr = builder.Configuration["MqttAgent:Port"];
        if (string.IsNullOrEmpty(portStr)) portStr = builder.Configuration["port"] ?? builder.Configuration["PORT"] ?? "23482";
        var port = int.Parse(portStr);
        
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port);
        });

        builder.Services.Configure<MqttAgent.Models.MqttOptions>(options =>
        {
            var section = builder.Configuration.GetSection("MqttAgent:Mqtt");
            
            options.Ip = section["Ip"] ?? string.Empty;
            if (string.IsNullOrEmpty(options.Ip)) options.Ip = builder.Configuration["MQTT_IP"] ?? "127.0.0.1";

            var portVal = section["Port"];
            if (string.IsNullOrEmpty(portVal)) portVal = builder.Configuration["MQTT_PORT"];
            options.Port = int.TryParse(portVal, out int p) ? p : 1883;

            options.User = section["User"] ?? string.Empty;
            if (string.IsNullOrEmpty(options.User)) options.User = builder.Configuration["MQTT_USER"] ?? string.Empty;

            options.Password = section["Password"] ?? string.Empty;
            if (string.IsNullOrEmpty(options.Password)) options.Password = builder.Configuration["MQTT_PW"] ?? string.Empty;

            options.EntityId = section["EntityId"] ?? string.Empty;
            if (string.IsNullOrEmpty(options.EntityId)) options.EntityId = builder.Configuration["MQTT_ENTITY_ID"] ?? "blu-pc";
        });

        builder.Services.AddSingleton(new TokenService(token));
        
        builder.Services.AddAuthentication("Token")
            .AddScheme<TokenAuthenticationSchemeOptions, TokenAuthenticationHandler>("Token", options => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers();

        // Add IpcMcp Services
        builder.Services.AddSingleton<ProcessService>();
        builder.Services.AddSingleton<RegistryService>();
        builder.Services.AddSingleton<WindowsService>();
        builder.Services.AddSingleton<LogonRegistryService>();
        builder.Services.AddSingleton<DeviceService>();
        builder.Services.AddSingleton<ScreenshotService>();
        builder.Services.AddSingleton<MultiMonitorToolService>();

        // Add Core Services
        builder.Services.AddSingleton<IDiscoveryService, DiscoveryService>();
        builder.Services.AddSingleton<IMqttManager, MqttManager>();
        builder.Services.AddSingleton<IPersistenceService, PersistenceService>();
        
        // Selectively enable background tasks based on mode
        builder.Services.AddSingleton<SystemMonitorService>();
        builder.Services.AddSingleton<ShutdownBlockerService>();
        builder.Services.AddSingleton<ForceActionService>();
        builder.Services.AddSingleton<NotificationReceiverService>();
        builder.Services.AddSingleton<ActionExecutorService>();

        if (isService)
        {
            builder.Host.UseWindowsService();
        }

        // Check for setup flags
        bool install = args.Contains("--install") || args.Contains("/install");
        bool uninstall = args.Contains("--uninstall") || args.Contains("/uninstall");
        bool moreStates = args.Contains("--more-states") || args.Contains("/more-states");
        bool isAdmin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        if ((install || uninstall || moreStates) && isAdmin)
        {
            // Create a temporary logger for setup tasks
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog());
            var setupLogger = loggerFactory.CreateLogger<PersistenceService>();
            var persistence = new PersistenceService(setupLogger);
            
            if (uninstall)
            {
                persistence.Uninstall();
                if (!install) return; // Exit if only uninstalling
            }
            
            if (install)
            {
                persistence.EnsureServiceSafeBoot();
            }
            
            if (moreStates)
            {
                persistence.EnsureMoreStatesTriggers();
            }

            // If we are JUST installing/uninstalling without starting, exit
            if (!isTray && !isService && !args.Contains("--run") && !args.Contains("--entity-state"))
            {
                Log.Information("Setup task complete. Exiting.");
                return;
            }
        }

        // Handle one-off state reporting
        var entityState = args.FirstOrDefault(a => a.StartsWith("--entity-state"))?.Split(' ', 2).LastOrDefault();
        if (string.IsNullOrEmpty(entityState)) entityState = args.SkipWhile(a => a != "--entity-state").Skip(1).FirstOrDefault();

        if (!string.IsNullOrEmpty(entityState))
        {
            var attributes = args.SkipWhile(a => a != "--entity-attributes").Skip(1).FirstOrDefault();
            
            using var appForState = builder.Build();
            var mqtt = appForState.Services.GetRequiredService<IMqttManager>();
            await mqtt.StartAsync(CancellationToken.None);
            
            var machineName = Environment.MachineName.ToLowerInvariant();
            var topic = $"homeassistant/select/{machineName}/state";
            await mqtt.EnqueueAsync(topic, entityState, true);

            if (!string.IsNullOrEmpty(attributes))
            {
                var attrTopic = $"homeassistant/select/{machineName}/attributes";
                await mqtt.EnqueueAsync(attrTopic, attributes, true);
            }

            await mqtt.StopAsync(CancellationToken.None);
            Log.Information("Reported state '{State}' to MQTT. Exiting.", entityState);
            return;
        }
        
        builder.Services.AddHostedService(p => p.GetRequiredService<SystemMonitorService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<NotificationReceiverService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<ActionExecutorService>());
        builder.Services.AddHostedService(p => p.GetRequiredService<ForceActionService>());
        
        // Always run MQTT if we aren't JUST a helper
        builder.Services.AddHostedService(p => (MqttManager)p.GetRequiredService<IMqttManager>());

        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        if (isTray)
        {
            // Tray app MUST run the shutdown blocker to handle user session logoff
            builder.Services.AddHostedService(p => p.GetRequiredService<ShutdownBlockerService>());
        }

        var app = builder.Build();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        
        app.MapGet("/", () => "MQTT.Agent is running.");

        if (isTray)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log.Information("Starting background services in tray-only mode...");
            
            try
            {
                // Start hosted services manually, skipping the web host itself
                var hostedServices = app.Services.GetServices<IHostedService>();
                foreach (var service in hostedServices)
                {
                    if (service.GetType().FullName?.Contains("GenericWebHostService") == true) continue;
                    _ = service.StartAsync(CancellationToken.None);
                }

                Application.Run(new TrayApplicationContext(app.Services));
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Tray application crashed unexpectedly: {Message}", ex.Message);
            }
        }
        else
        {
            Log.Information("Starting Web Host...");
            app.Run();
        }
    }
}
