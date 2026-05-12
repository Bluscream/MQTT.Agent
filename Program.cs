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
using Microsoft.OpenApi.Models;

namespace MqttAgent;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static async Task Main(string[] args)
    {
        if (Global.IsAnyHelper)
        {
            SessionHelper.Run(args);
            return;
        }

        AttachConsole(ATTACH_PARENT_PROCESS);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddJsonFile("MqttAgent.json", optional: true, reloadOnChange: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        Config.Initialize(builder.Configuration);

        // Configure Logging
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(baseDir, "logs", "mqtt-agent.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog();

        // Extract args and config from Global
        bool isTray = Global.IsTrayMode;
        bool isService = Global.IsServiceMode;
        
        var token = Config.Get("token", "-token", "MQTTAGENT_TOKEN");
        
        if (string.IsNullOrEmpty(token))
        {
            Log.Fatal("CRITICAL ERROR: No MQTTAGENT_TOKEN provided. The application cannot start without an authentication token for security reasons. Please set the MQTTAGENT_TOKEN environment variable or provide it via appsettings.json.");
            throw new InvalidOperationException("MQTTAGENT_TOKEN is required.");
        }

        // Configure Kestrel
        var portStr = Config.Get("port", "MQTTAGENT_PORT");
        if (string.IsNullOrEmpty(portStr)) portStr = "23482";
        var port = int.Parse(portStr);
        
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port);
        });

        builder.Services.Configure<MqttAgent.Models.MqttOptions>(options =>
        {
            options.Ip = Config.Get("Mqtt:Ip", "MQTT_IP");
            if (string.IsNullOrEmpty(options.Ip)) options.Ip = "127.0.0.1";

            var portVal = Config.Get("Mqtt:Port", "MQTT_PORT");
            options.Port = int.TryParse(portVal, out int p) ? p : 1883;

            options.User = Config.Get("Mqtt:User", "MQTT_USER");
            options.Password = Config.Get("Mqtt:Password", "MQTT_PW");
            options.EntityId = Config.Get("Mqtt:EntityId", "MQTT_ENTITY_ID");
            if (string.IsNullOrEmpty(options.EntityId)) options.EntityId = Global.SafeMachineName;
        });

        builder.Services.AddSingleton(new TokenService(token));
        
        builder.Services.AddAuthentication("Token")
            .AddScheme<TokenAuthenticationSchemeOptions, TokenAuthenticationHandler>("Token", options => { });
        builder.Services.AddAuthorization();
        
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "MQTT.Agent API", Version = "v1" });
            c.AddSecurityDefinition("Token", new OpenApiSecurityScheme
            {
                Description = "Token Authentication using 'Authorization: Bearer <token>' header or '?token=<token>' query parameter",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer"
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Token" }
                    },
                    Array.Empty<string>()
                }
            });
        });

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
        builder.Services.AddSingleton<ActionCenterPollerService>();
        builder.Services.AddSingleton<CameraService>();
        builder.Services.AddSingleton<TrayStarterService>();

        if (isService)
        {
            builder.Host.UseWindowsService();
        }

        // Use Global setup flags
        bool install = Global.IsInstall;
        bool uninstall = Global.IsUninstall;
        bool moreStates = Global.IsMoreStatesEnabled;
        bool isAdmin = Global.IsAdmin;

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
                persistence.EnsureFirewallRule(port);
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
        var entityState = args.FirstOrDefault(a => a.StartsWith(Global.Args.EntityState))?.Split(' ', 2).LastOrDefault();
        if (string.IsNullOrEmpty(entityState)) entityState = args.SkipWhile(a => a != Global.Args.EntityState).Skip(1).FirstOrDefault();

        if (!string.IsNullOrEmpty(entityState))
        {
            var attributes = args.SkipWhile(a => a != Global.Args.EntityAttributes).Skip(1).FirstOrDefault();
            
            using var appForState = builder.Build();
            var mqtt = appForState.Services.GetRequiredService<IMqttManager>();
            await mqtt.StartAsync(CancellationToken.None);
            
            var machineName = Global.UniqueId;
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
        builder.Services.AddHostedService(p => p.GetRequiredService<ActionCenterPollerService>());
        
        if (Global.IsMoreStatesEnabled)
        {
            builder.Services.AddHostedService(p => p.GetRequiredService<CameraService>());
        }
        builder.Services.AddHostedService(p => p.GetRequiredService<TrayStarterService>());
        
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

        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "MQTT.Agent API v1");
            c.RoutePrefix = "docs";
        });

        app.MapControllers();
        
        app.MapGet("/", () => Results.Redirect("/docs"));

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
