using CodexFileInspector;
using CodexFileInspector.Contracts;
using CodexFileInspector.DirectoryListing;
using CodexFileInspector.Globbing;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Reading;
using CodexFileInspector.Ripgrep;
using CodexFileInspector.Searching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

string assemblyVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
if (args is ["--version"])
{
    Console.WriteLine($"Codex File Inspector {assemblyVersion}");
    return;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IFileSystemPlatform, WindowsFileSystemPlatform>();
builder.Services.AddSingleton<IProcessPlatform, WindowsJobProcessPlatform>();
builder.Services.AddSingleton<ToolRequestValidator>();
builder.Services.AddSingleton<ReadFileService>();
builder.Services.AddSingleton<ListDirectoryService>();
builder.Services.AddSingleton<IRipgrepRunner, RipgrepRunner>();
builder.Services.AddSingleton<RipgrepInvocationBuilder>();
builder.Services.AddSingleton<GlobService>();
builder.Services.AddSingleton<GrepService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "codex-file-inspector",
            Version = assemblyVersion,
        };
        options.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
