using System.Net;
using CodexFileInspector.Contracts;
using CodexFileInspector.Diagnostics;
using CodexFileInspector.DirectoryListing;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Reading;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using CodexFileInspector.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace CodexFileInspector.Hosting;

internal static class ServerHost
{
    public static IHost CreateStdioHost(ServerCommandLine commandLine)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        ConfigureServices(builder.Services, builder.Logging, commandLine.DiagnosticsEnabled)
            .WithStdioServerTransport();
        return builder.Build();
    }

    public static WebApplication CreateHttpApplication(
        ServerCommandLine commandLine,
        Action<IServiceCollection>? configureServices = null)
    {
        // The caller's cwd, environment and appsettings are inspected data, not
        // HTTP host configuration. Only our explicit startup options configure it.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(ServerHost).Assembly.GetName().Name,
            Args = [],
        });
        builder.Host.UseConsoleLifetime();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, commandLine.Port));
        builder.Services.AddRouting();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new HttpEndpointPolicy(commandLine.Port));
        builder.Services.AddSingleton<HttpToolCancellation>();
        builder.Services.AddOptions<McpServerOptions>().Configure<HttpToolCancellation>(
            (options, cancellation) => options.Filters.Request.CallToolFilters.Add(cancellation.Wrap));
        ConfigureServices(builder.Services, builder.Logging, commandLine.DiagnosticsEnabled)
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);
        // Retain lifecycle/error messages without collecting successful HTTP requests.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
        configureServices?.Invoke(builder.Services);
        WebApplication application = builder.Build();
        application.Use(async (context, next) =>
        {
            if (!context.RequestServices.GetRequiredService<HttpEndpointPolicy>().Allows(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        application.MapMcp("/mcp");
        return application;
    }

    private static IMcpServerBuilder ConfigureServices(
        IServiceCollection services, ILoggingBuilder logging, bool diagnosticsEnabled)
    {
        logging.ClearProviders();
        logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        services.AddSingleton<IDiagnosticWriter>(_ => diagnosticsEnabled
            ? new DiagnosticFileWriter(Path.Combine(AppContext.BaseDirectory, "logs"))
            : DisabledDiagnosticWriter.Instance);
        services.AddSingleton<IFileSystemPlatform, WindowsFileSystemPlatform>();
        services.AddSingleton<IProcessPlatform, WindowsJobProcessPlatform>();
        services.AddSingleton<ToolRequestValidator>();
        services.AddSingleton<ReadFileService>();
        services.AddSingleton<ListDirectoryService>();
        services.AddSingleton<IRipgrepRunner, RipgrepRunner>();
        services.AddSingleton<RipgrepInvocationBuilder>();
        services.AddSingleton<GlobService>();
        services.AddSingleton<GrepService>();
        return services.AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "codex-file-inspector",
                Version = typeof(ServerHost).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            };
            options.ServerInstructions = ServerInstructions.Text;
        }).WithToolsFromAssembly(typeof(FileInspectorTools).Assembly);
    }
}
