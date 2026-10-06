using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodexFileInspector.Tests;

internal sealed class HttpServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private HttpServerProcess(Process process, int port)
    {
        _process = process;
        Endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
        _process.OutputDataReceived += RecordOutput;
        _process.ErrorDataReceived += RecordOutput;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // HTTP lifetime must not depend on the stdin pipe owned by a client.
        _process.StandardInput.Close();
    }

    public Uri Endpoint { get; }
    public string Output => ReadOutput();

    public static async Task<HttpServerProcess> StartAsync(string workingDirectory)
    {
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        HttpServerProcess server = new(Process.Start(CreateStartInfo(workingDirectory, port))
            ?? throw new InvalidOperationException("Could not start the HTTP test server."), port);
        try
        {
            await server.WaitForReadyAsync();
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string workingDirectory, int port)
    {
        ProcessStartInfo startInfo = new(ResolveServerExecutable())
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Exercise a standalone local service, rather than relying on the
        // user's proxy, ASP.NET Core environment, or appsettings configuration.
        startInfo.Environment.Remove("HTTP_PROXY");
        startInfo.Environment.Remove("HTTPS_PROXY");
        startInfo.Environment.Remove("ALL_PROXY");
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Audit";
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Audit";
        return startInfo;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(timeout.Token);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private async Task WaitForReadyAsync()
    {
        using HttpClient client = CreateHttpClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"HTTP server exited with {_process.ExitCode}: {ReadOutput()}");
            }

            try
            {
                // Any HTTP response proves Kestrel is listening. MCP itself is
                // negotiated by the SDK in each test, after this readiness probe.
                using HttpResponseMessage response = await client.GetAsync(
                    Endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50, timeout.Token);
            }
        }

        throw new TimeoutException($"HTTP server did not listen in time: {ReadOutput()}");
    }

    private void RecordOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is { } line)
        {
            lock (_output)
            {
                // Keep a bounded failure diagnostic without retaining an
                // unbounded child-process log throughout a test run.
                if (_output.Length < 32_768)
                {
                    _output.AppendLine(line);
                }
            }
        }
    }

    private string ReadOutput()
    {
        lock (_output)
        {
            return _output.ToString();
        }
    }

    private static string ResolveServerExecutable() =>
        Environment.GetEnvironmentVariable("CODEX_FILE_INSPECTOR_SERVER_PATH") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "CodexFileInspector.exe");
}
