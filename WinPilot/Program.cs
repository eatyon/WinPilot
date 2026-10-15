using System.Net.Sockets;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.ResponseCompression;
using WinPilot.Core;
using WinPilot.Core.Desktop;
using WinPilot.Core.Input;
using WinPilot.Features.Desktop;
using WinPilot.Features.Files;
using WinPilot.Features.Logs;
using WinPilot.Features.Programs;
using WinPilot.Features.Tasks;
using WinPilot.Features.Terminal;
using WinPilot.Features.Updates;
using WinPilot.Features.Notifications;
using WinPilot.Features.Notifications.CommandBots;
using WinPilot.Features.Notifications.CommandBots.Commands;
using WinPilot.Features.Plugins;
using WinPilot.Features.Security;
using WinPilot.Core.Programs;
using WinPilot.Core.SystemStatus;
using WinPilot.PluginContract;
using WinPilot.Infrastructure;
using WinPilot.Web;
using WinPilot.Windows;

namespace WinPilot;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        using var singleInstanceGuard = SingleInstanceGuard.TryAcquire();
        if (singleInstanceGuard is null)
        {
            WriteExistingInstanceLog();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        PortableDataPaths paths;
        WinPilotSettings settings;
        try
        {
            paths = PortableDataPaths.Create(AppContext.BaseDirectory);
            settings = WinPilotSettings.LoadAndInitialize(paths.ConfigurationFile);
        }
        catch (Exception exception)
        {
            ShowStartupError("WinPilot 初始化失败", exception.Message);
            return;
        }

        using var fileLoggerProvider = new RollingFileLoggerProvider(paths.LogsDirectory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(fileLoggerProvider);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        var listenHost = settings.LanAccessEnabled ? "0.0.0.0" : "127.0.0.1";
        builder.WebHost.UseUrls($"http://{listenHost}:{settings.Port}");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["text/html", "text/plain", "text/css", "text/javascript", "application/javascript", "application/json", "application/problem+json", "image/svg+xml"];
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
            options.Level = System.IO.Compression.CompressionLevel.Fastest);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IDesktopScreenshotProvider, GdiDesktopScreenshotProvider>();
        builder.Services.AddSingleton<DesktopScreenshotService>();
        builder.Services.AddSingleton<IKeyboardInputSimulator, SendInputKeyboardInputSimulator>();
        builder.Services.AddSingleton<KeyboardInputService>();
        builder.Services.AddSingleton<FileSystemService>();
        builder.Services.AddSingleton(_ => new LogQueryService(paths));
        builder.Services.AddSingleton(_ => new ProgramTagService(paths));
        builder.Services.AddSingleton<IProgramManager, WindowsProgramManager>();
        builder.Services.AddSingleton<WindowsProcessEventListener>();
        builder.Services.AddSingleton<TaskService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskService>());
        builder.Services.AddSingleton<ISystemResourceMonitor, WindowsSystemResourceMonitor>();
        builder.Services.AddSingleton<TerminalSessionManager>();
        builder.Services.AddSingleton<StartupTaskService>();
        builder.Services.AddSingleton<INotificationService, NotificationService>();
        builder.Services.AddSingleton<CommandBotConfiguration>();
        builder.Services.AddSingleton<TelegramCommandBotService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramCommandBotService>());
        builder.Services.AddSingleton<FeishuCommandBotService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FeishuCommandBotService>());
        builder.Services.AddSingleton<QQBotCommandBotService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<QQBotCommandBotService>());
        builder.Services.AddSingleton<OneBotCommandBotService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OneBotCommandBotService>());
        builder.Services.AddSingleton<PluginTaskTriggerRegistry>();
        builder.Services.AddSingleton<PluginNotificationVariableRegistry>();
        builder.Services.AddSingleton<PluginTaskActionRegistry>();
        builder.Services.AddSingleton<PluginTaskExtensionRegistry>();
        builder.Services.AddSingleton(provider => new PluginCommandRegistry(CommandProcessor.IsSupportedCommand,
            provider.GetRequiredService<ILogger<PluginCommandRegistry>>()));
        builder.Services.AddSingleton<TaskVariableCatalog>();
        builder.Services.AddSingleton<IPluginHostActionApi, PluginHostActionApi>();
        builder.Services.AddSingleton<IPluginProcessApi, PluginProcessApi>();
        builder.Services.AddSingleton<IPluginHostSecurityApi, PluginHostSecurityApi>();
        builder.Services.AddSingleton<CSharpPluginRuntime>();
        builder.Services.AddSingleton<IPluginRuntime>(provider => provider.GetRequiredService<CSharpPluginRuntime>());
        builder.Services.AddSingleton<PluginPageSessionManager>();
        builder.Services.AddSingleton<PluginManager>();
        builder.Services.AddSingleton<PluginWebResourceService>();
        builder.Services.AddSingleton<AuthStore>();
        builder.Services.AddSingleton<SessionManager>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<SecurityEventNotifier>();
        builder.Services.AddSingleton<SafeModeService>();
        builder.Services.AddSingleton<WebFacadeService>();
        builder.Services.AddSingleton<UpdateService>();

        var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(Program).FullName!);
        var webLogger = loggerFactory.CreateLogger(typeof(HealthEndpointRouteBuilderExtensions).FullName!);

        logger.LogInformation("WinPilot 服务正在初始化，操作系统：{OperatingSystem}", Environment.OSVersion.VersionString);
        logger.LogDebug("用户设置已加载并完成检查");
        app.Services.GetRequiredService<SecurityEventNotifier>().ObserveAuthConfiguration(app.Services.GetRequiredService<AuthStore>().IsConfigured);

        if (!AdministratorContext.IsAdministrator())
        {
            logger.LogCritical("WinPilot 必须以管理员权限运行");
            ShowStartupError("需要管理员权限", "请通过 WinPilot.exe 启动并接受 UAC 提示");
            await app.DisposeAsync();
            return;
        }
        try
        {
            app.UseMiddleware<RequestCancellationMiddleware>();
            app.UseResponseCompression();
            app.UseMiddleware<AuthenticationMiddleware>();
            app.UseMiddleware<SafeModeMiddleware>();
            app.UseDefaultFiles();
            var staticFileOptions = new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
                        context.Context.Response.Headers.CacheControl = "no-cache";
                    else if (context.Context.Request.Path.StartsWithSegments("/assets"))
                        context.Context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
                }
            };
            app.UseStaticFiles(staticFileOptions);
            app.UseWebSockets();
            app.MapHealthEndpoint();
            app.MapUpdateEndpoints();
            app.MapAuthenticationEndpoints();
            app.MapSafeModeEndpoints();
            app.MapDesktopEndpoints();
            app.MapDesktopKeyboardEndpoints();
            app.MapFileEndpoints();
            app.MapLogEndpoints();
            app.MapSettingsEndpoints(paths.ConfigurationFile, app.Services.GetRequiredService<StartupTaskService>());
            app.MapNotificationEndpoints(paths.ConfigurationFile, app.Services.GetRequiredService<INotificationService>());
            app.MapCommandBotEndpoints();
            app.MapProgramEndpoints();
            app.MapTaskEndpoints();
            app.MapSystemStatusEndpoints();
            app.MapTerminalEndpoints();
            app.MapPluginEndpoints();
            app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "API 不存在"));
            app.MapFallbackToFile("index.html", staticFileOptions);

            using var trayContext = new TrayApplicationContext(
                new Uri($"http://127.0.0.1:{settings.Port}/"),
                loggerFactory.CreateLogger<TrayApplicationContext>(),
                app.Services.GetRequiredService<SafeModeService>(),
                paths,
                singleInstanceGuard.BeginShutdown);

            await app.StartAsync();
            webLogger.LogInformation("Web 服务已监听：http://{Host}:{Port}", listenHost, settings.Port);

            _ = app.Services.GetRequiredService<PluginManager>();
            app.Services.GetRequiredService<TaskService>().NotifyPluginsLoaded();
            if (args.Length == 1 && args[0] == "--update-completed")
                loggerFactory.CreateLogger<UpdateService>().LogInformation("更新完成，版本：{Version}", WinPilotVersion.DisplayVersion);
            Application.Run(trayContext);
        }
        catch (Exception exception) when (IsAddressAlreadyInUse(exception))
        {
            logger.LogCritical(exception, "端口 {Port} 已被其他程序占用", settings.Port);
            ShowStartupError("WinPilot 启动失败", $"端口 {settings.Port} 已被其他程序占用，请在 User\\config.json 中修改 port 后重新启动 WinPilot");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "WinPilot 后台服务启动或运行失败");
            ShowStartupError("WinPilot 运行失败", exception.Message);
        }
        finally
        {
            try
            {
                await app.StopAsync();
                logger.LogInformation("WinPilot 服务已停止");
            }
            finally { await app.DisposeAsync(); }
        }
    }

    private static void ShowStartupError(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static bool IsAddressAlreadyInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteExistingInstanceLog()
    {
        using var loggerProvider = new RollingFileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs"));
        loggerProvider.CreateLogger(typeof(SingleInstanceGuard).FullName!)
            .LogInformation("检测到 WinPilot 已在运行，本次启动退出");
    }
}
