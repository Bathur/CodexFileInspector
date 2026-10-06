using CodexFileInspector.Hosting;
using Microsoft.Extensions.Hosting;

string assemblyVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
ServerCommandLine commandLine;
try
{
    commandLine = ServerCommandLine.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(ServerCommandLine.Usage);
    Environment.ExitCode = 2;
    return;
}
if (commandLine.ShowVersion)
{
    Console.WriteLine($"Codex File Inspector {assemblyVersion}");
    return;
}

if (commandLine.ShowHelp)
{
    Console.WriteLine(ServerCommandLine.Usage);
    return;
}
if (commandLine.Transport == ServerTransport.Http)
{
    await using var application = ServerHost.CreateHttpApplication(commandLine);
    await application.RunAsync().ConfigureAwait(false);
}
else
{
    using var host = ServerHost.CreateStdioHost(commandLine);
    await host.RunAsync().ConfigureAwait(false);
}
