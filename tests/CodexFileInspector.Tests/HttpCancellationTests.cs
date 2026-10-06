using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodexFileInspector.Hosting;
using CodexFileInspector.Platform;
using CodexFileInspector.Platform.Windows;
using CodexFileInspector.Ripgrep;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class HttpCancellationTests
{
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task Client_cancellation_terminates_and_disposes_inflight_ripgrep(string protocolVersion)
    {
        using TestWorkspace workspace = new();
        using BlockedRipgrepPlatform processes = new();
        await using HttpApplicationFixture server = await HttpApplicationFixture.StartAsync(processes);
        SessionCaptureHandler capture = new();
        using HttpClient http = new(capture) { Timeout = Timeout.InfiniteTimeSpan };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using McpClient client = await CreateClientAsync(server.Endpoint, http, protocolVersion, timeout.Token);
        using CancellationTokenSource callCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

        Task<CallToolResult> call = client.CallToolAsync("grep", Arguments(workspace.Root),
            cancellationToken: callCancellation.Token).AsTask();
        await processes.Started.WaitAsync(timeout.Token);
        if (protocolVersion == "2025-06-18")
        {
            // The SDK HTTP send awaits the response body before registering its
            // automatic cancellation notification. Exercise the legacy wire
            // signal explicitly, then stop the client's response-body await.
            await client.SendNotificationAsync("notifications/cancelled",
                new CancelledNotificationParams { RequestId = capture.ToolRequestId!.Value },
                cancellationToken: timeout.Token);
        }
        callCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await processes.Disposed.WaitAsync(timeout.Token);
        Assert.True(processes.Terminated);
        Assert.True(processes.Observer!.HasExited);
        Assert.Equal(4, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task Application_shutdown_terminates_and_disposes_inflight_ripgrep(string protocolVersion)
    {
        using TestWorkspace workspace = new();
        using BlockedRipgrepPlatform processes = new();
        await using HttpApplicationFixture server = await HttpApplicationFixture.StartAsync(processes);
        using HttpClient http = HttpServerProcess.CreateHttpClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using McpClient client = await CreateClientAsync(server.Endpoint, http, protocolVersion, timeout.Token);

        Task<CallToolResult> call = client.CallToolAsync("grep", Arguments(workspace.Root),
            cancellationToken: timeout.Token).AsTask();
        await processes.Started.WaitAsync(timeout.Token);
        server.Application.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await processes.Disposed.WaitAsync(timeout.Token);
        Assert.True(processes.Terminated);
        Assert.True(processes.Observer!.HasExited);
        await server.Application.StopAsync(timeout.Token);
        _ = await Record.ExceptionAsync(() => call);
    }

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task Disconnection_uses_the_protocol_specific_cancellation_rule(string protocolVersion)
    {
        using TestWorkspace workspace = new();
        using BlockedRipgrepPlatform processes = new();
        await using HttpApplicationFixture server = await HttpApplicationFixture.StartAsync(processes);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        SessionCaptureHandler capture = new();
        using HttpClient http = new(capture) { Timeout = Timeout.InfiniteTimeSpan };
        await using McpClient client = await CreateClientAsync(server.Endpoint, http, protocolVersion, timeout.Token);
        if (protocolVersion == "2025-06-18")
        {
            Assert.NotNull(capture.SessionId);
        }
        else
        {
            Assert.Null(capture.SessionId);
        }
        using TcpClient socket = new();
        await socket.ConnectAsync(IPAddress.Loopback, server.Endpoint.Port, timeout.Token);
        string body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1042,
            method = "tools/call",
            @params = new
            {
                name = "grep",
                arguments = Arguments(workspace.Root),
                _meta = protocolVersion == "2026-07-28" ? new Dictionary<string, object?>
                {
                    ["io.modelcontextprotocol/protocolVersion"] = protocolVersion,
                    ["io.modelcontextprotocol/clientInfo"] = new { name = "disconnect test", version = "1" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new { },
                } : null,
            },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string headers = $"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{server.Endpoint.Port}\r\n" +
            "Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\n" +
            "X-Test-Disconnect: true\r\n" +
            $"MCP-Protocol-Version: {protocolVersion}\r\nMcp-Method: tools/call\r\nMcp-Name: grep\r\n" +
            (capture.SessionId is { } sessionId ? $"Mcp-Session-Id: {sessionId}\r\n" : string.Empty) +
            $"Content-Length: {payload.Length}\r\n\r\n";
        NetworkStream stream = socket.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);
        await stream.WriteAsync(payload, timeout.Token);
        await processes.Started.WaitAsync(timeout.Token);

        // New HTTP cancels on stream closure. The initialize-era specification
        // deliberately separates a disconnection from an explicit cancellation.
        socket.Client.LingerState = new LingerOption(true, 0);
        socket.Dispose();
        await server.RequestDisconnected.WaitAsync(timeout.Token);

        if (protocolVersion == "2025-06-18")
        {
            Assert.False(processes.Disposed.IsCompleted);
            await client.SendNotificationAsync("notifications/cancelled",
                new CancelledNotificationParams { RequestId = new RequestId(1042) },
                cancellationToken: timeout.Token);
        }

        await processes.Disposed.WaitAsync(timeout.Token);
        Assert.True(processes.Terminated);
        Assert.True(processes.Observer!.HasExited);
        // The legacy session remains usable after just one POST is dropped.
        Assert.Equal(4, (await client.ListToolsAsync(cancellationToken: timeout.Token)).Count);
    }

    private static Dictionary<string, object?> Arguments(string root) => new()
    {
        ["path"] = root,
        ["pattern"] = "needle",
        ["pattern_kind"] = "literal",
        ["respect_ignore_files"] = false,
    };

    private static Task<McpClient> CreateClientAsync(Uri endpoint, HttpClient http,
        string protocolVersion, CancellationToken token) => McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
        }, http), new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: token);

    private sealed class SessionCaptureHandler() : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        public string? SessionId { get; private set; }
        public RequestId? ToolRequestId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Content is { } content)
            {
                using JsonDocument body = JsonDocument.Parse(await content.ReadAsStringAsync(token));
                if (body.RootElement.TryGetProperty("method", out JsonElement method) && method.GetString() == "tools/call")
                {
                    JsonElement id = body.RootElement.GetProperty("id");
                    ToolRequestId = id.ValueKind == JsonValueKind.String
                        ? new RequestId(id.GetString()!) : new RequestId(id.GetInt64());
                }
            }
            HttpResponseMessage response = await base.SendAsync(request, token);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values))
            {
                SessionId = values.Single();
            }

            return response;
        }
    }

    private sealed class HttpApplicationFixture(WebApplication application, Uri endpoint, Task requestDisconnected) : IAsyncDisposable
    {
        public WebApplication Application { get; } = application;
        public Uri Endpoint { get; } = endpoint;
        public Task RequestDisconnected { get; } = requestDisconnected;

        public static async Task<HttpApplicationFixture> StartAsync(IProcessPlatform platform)
        {
            using TcpListener reservation = new(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            WebApplication application = ServerHost.CreateHttpApplication(
                ServerCommandLine.Parse(["--transport", "http", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
                services =>
                {
                    services.RemoveAll<IProcessPlatform>();
                    services.AddSingleton(platform);
                });
            TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            application.Use(async (context, next) =>
            {
                bool monitored = context.Request.Headers.ContainsKey("X-Test-Disconnect");
                using CancellationTokenRegistration registration = monitored
                    ? context.RequestAborted.Register(() => disconnected.TrySetResult()) : default;
                try
                {
                    await next(context);
                }
                finally
                {
                    // A later SDK cancellation callback can complete next and
                    // dispose our registration before Cancel invokes it.
                    if (monitored && context.RequestAborted.IsCancellationRequested)
                        disconnected.TrySetResult();
                }
            });
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                await application.StartAsync(timeout.Token);
                return new HttpApplicationFixture(application, new Uri($"http://127.0.0.1:{port}/mcp"), disconnected.Task);
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            try
            {
                await Application.StopAsync(timeout.Token);
            }
            finally
            {
                await Application.DisposeAsync();
            }
        }
    }

    private sealed class BlockedRipgrepPlatform : IProcessPlatform, IDisposable
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public Task Disposed => _disposed.Task;
        public bool Terminated { get; private set; }
        public Process? Observer { get; private set; }

        public IRunningProcess Start(ProcessStartRequest request)
        {
            // The real bundled rg, real Windows Job, and real RipgrepRunner are
            // retained. Only the test argv/stdin policy is changed to create a
            // deterministic in-flight search without a giant timing-sensitive
            // file corpus or a test executable in the production package.
            IRunningProcess running = new WindowsJobProcessPlatform().Start(new ProcessStartRequest(
                BundledRipgrep.RequireExecutable(), ["--no-config", "needle", "-"],
                request.WorkingDirectory, KeepStandardInputOpen: true));
            Observer = Process.GetProcessById(running.Id);
            _ = Observer.Handle;
            _started.TrySetResult();
            return new ObservedProcess(running, this);
        }

        public void Dispose()
        {
            if (Observer is { } process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        _ = process.WaitForExit(5_000);
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private sealed class ObservedProcess(IRunningProcess inner, BlockedRipgrepPlatform owner) : IRunningProcess
        {
            public int Id => inner.Id;
            public int? CompletedExitCode => inner.CompletedExitCode;
            public StreamReader StandardOutput => inner.StandardOutput;
            public StreamReader StandardError => inner.StandardError;
            public ValueTask<int> WaitForExitAsync(CancellationToken token) => inner.WaitForExitAsync(token);

            public async ValueTask TerminateAsync(CancellationToken token)
            {
                await inner.TerminateAsync(token);
                owner.Terminated = true;
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await inner.DisposeAsync();
                    owner._disposed.TrySetResult();
                }
                catch (Exception exception)
                {
                    owner._disposed.TrySetException(exception);
                    throw;
                }
            }
        }
    }
}
