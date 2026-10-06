using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexFileInspector.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class HttpServerTests
{
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task Both_protocol_generations_discover_and_invoke_all_four_tools(string protocolVersion)
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("sample.txt");
        await File.WriteAllTextAsync(path, "one\ntwo");
        // Malformed consumer configuration must not become server configuration.
        await File.WriteAllTextAsync(workspace.PathFor("appsettings.json"), "{ malformed JSON");
        await File.WriteAllTextAsync(workspace.PathFor("appsettings.Audit.json"), "{ malformed JSON");
        await using HttpServerProcess server = await HttpServerProcess.StartAsync(workspace.Root);
        using HttpClient http = HttpServerProcess.CreateHttpClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using McpClient client = await CreateClientAsync(server.Endpoint, http, protocolVersion, timeout.Token);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(["glob", "grep", "list_directory", "read_file"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Equal(ServerInstructions.Text, client.ServerInstructions);
        foreach (Tool expected in ToolContractCatalog.Create().Select(tool => tool.ProtocolTool))
        {
            Tool actual = tools.Single(tool => tool.Name == expected.Name).ProtocolTool;
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Description, actual.Description);
            Assert.True(JsonElement.DeepEquals(expected.InputSchema, actual.InputSchema));
            Assert.NotNull(actual.OutputSchema);
            Assert.True(JsonElement.DeepEquals(expected.OutputSchema!.Value, actual.OutputSchema.Value));
            Assert.Equal(expected.Annotations?.ReadOnlyHint, actual.Annotations?.ReadOnlyHint);
            Assert.Equal(expected.Annotations?.DestructiveHint, actual.Annotations?.DestructiveHint);
            Assert.Equal(expected.Annotations?.IdempotentHint, actual.Annotations?.IdempotentHint);
            Assert.Equal(expected.Annotations?.OpenWorldHint, actual.Annotations?.OpenWorldHint);
        }

        CallToolResult read = await client.CallToolAsync("read_file",
            new Dictionary<string, object?> { ["path"] = path }, cancellationToken: timeout.Token);
        AssertSuccess(read);
        Assert.Equal("one\ntwo", read.StructuredContent?.GetProperty("content").GetString());

        CallToolResult list = await client.CallToolAsync("list_directory",
            new Dictionary<string, object?> { ["path"] = workspace.Root }, cancellationToken: timeout.Token);
        AssertSuccess(list);
        Assert.Contains(list.StructuredContent!.Value.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == "sample.txt");

        CallToolResult glob = await client.CallToolAsync("glob", new Dictionary<string, object?>
        {
            ["path"] = workspace.Root,
            ["include_globs"] = new[] { "**/*.txt" },
            ["respect_ignore_files"] = false,
        }, cancellationToken: timeout.Token);
        AssertSuccess(glob);
        Assert.Equal(path, Assert.Single(glob.StructuredContent!.Value.GetProperty("paths").EnumerateArray()).GetString());

        CallToolResult grep = await client.CallToolAsync("grep", new Dictionary<string, object?>
        {
            ["path"] = workspace.Root,
            ["pattern"] = "two",
            ["pattern_kind"] = "literal",
            ["respect_ignore_files"] = false,
        }, cancellationToken: timeout.Token);
        AssertSuccess(grep);
        Assert.Equal(1, grep.StructuredContent?.GetProperty("returned_results").GetInt32());

        CallToolResult invalid = await client.CallToolAsync("read_file",
            new Dictionary<string, object?> { ["path"] = "relative.txt" }, cancellationToken: timeout.Token);
        Assert.True(invalid.IsError);
        Assert.Equal("path_not_absolute", invalid.StructuredContent?.GetProperty("error").GetProperty("code").GetString());
        AssertCanonicalText(invalid);

        CallToolResult missing = await client.CallToolAsync("read_file",
            new Dictionary<string, object?> { ["path"] = workspace.PathFor("missing.txt") }, cancellationToken: timeout.Token);
        Assert.True(missing.IsError);
        Assert.Equal("path_missing", missing.StructuredContent?.GetProperty("error").GetProperty("code").GetString());
        Assert.False(missing.StructuredContent?.GetProperty("error").GetProperty("retryable").GetBoolean());
        AssertCanonicalText(missing);
        Assert.DoesNotContain("Request starting", server.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("sample.txt", server.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Occupied_port_fails_startup_instead_of_binding_a_different_address()
    {
        using TestWorkspace workspace = new();
        using TcpListener occupied = new(IPAddress.Loopback, 0);
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using Process process = Process.Start(HttpServerProcess.CreateStartInfo(workspace.Root, port))!;
        process.StandardInput.Close();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("AddressInUseException", await errors, StringComparison.Ordinal);
            _ = await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Independent_legacy_and_new_clients_can_call_the_same_service_concurrently()
    {
        using TestWorkspace workspace = new();
        string path = workspace.PathFor("concurrent.txt");
        await File.WriteAllTextAsync(path, "shared service");
        await using HttpServerProcess server = await HttpServerProcess.StartAsync(workspace.Root);
        using HttpClient firstHttp = HttpServerProcess.CreateHttpClient();
        using HttpClient secondHttp = HttpServerProcess.CreateHttpClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using McpClient legacy = await CreateClientAsync(server.Endpoint, firstHttp, "2025-06-18", timeout.Token);
        await using McpClient current = await CreateClientAsync(server.Endpoint, secondHttp, "2026-07-28", timeout.Token);

        string[] callTools = Enumerable.Range(0, 12)
            .Select(index => (index % 3) switch { 0 => "read_file", 1 => "grep", _ => "glob" }).ToArray();
        Task<CallToolResult>[] calls = callTools.Select((tool, index) =>
        {
            Dictionary<string, object?> arguments = new() { ["path"] = tool == "read_file" ? path : workspace.Root };
            if (tool == "grep")
            {
                arguments["pattern"] = "shared";
                arguments["pattern_kind"] = "literal";
                arguments["respect_ignore_files"] = false;
            }
            else if (tool == "glob")
            {
                arguments["include_globs"] = new[] { "**/*.txt" };
                arguments["respect_ignore_files"] = false;
            }

            return (index % 2 == 0 ? legacy : current).CallToolAsync(tool,
                arguments, cancellationToken: timeout.Token).AsTask();
        }).ToArray();
        CallToolResult[] results = await Task.WhenAll(calls);
        for (int index = 0; index < results.Length; index++)
        {
            CallToolResult result = results[index];
            AssertSuccess(result);
            if (callTools[index] == "read_file")
            {
                Assert.Equal("shared service", result.StructuredContent?.GetProperty("content").GetString());
            }
            else
            {
                Assert.Equal(1, result.StructuredContent?.GetProperty("returned_results").GetInt32());
            }
        }

        // Ending a legacy session must leave the independently running service
        // and the other client's requests available.
        await legacy.DisposeAsync();
        Assert.Equal(4, (await current.ListToolsAsync(cancellationToken: timeout.Token)).Count);
        using HttpClient replacementHttp = HttpServerProcess.CreateHttpClient();
        await using McpClient replacement = await CreateClientAsync(
            server.Endpoint, replacementHttp, "2025-06-18", timeout.Token);
        Assert.Equal(4, (await replacement.ListToolsAsync(cancellationToken: timeout.Token)).Count);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("same", null, true)]
    [InlineData("localhost", "localhost", true)]
    [InlineData("http://example.test", null, false)]
    [InlineData("https://127.0.0.1", null, false)]
    [InlineData("wrong-port", null, false)]
    [InlineData("null", null, false)]
    [InlineData("multiple", null, false)]
    [InlineData(null, "example.test", false)]
    public async Task Endpoint_checks_host_and_origin_without_requiring_authentication(
        string? originKind, string? host, bool allowed)
    {
        using TestWorkspace workspace = new();
        await using HttpServerProcess server = await HttpServerProcess.StartAsync(workspace.Root);
        using HttpClient http = HttpServerProcess.CreateHttpClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpRequestMessage request = new(HttpMethod.Post, server.Endpoint);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/list");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"endpoint test\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}",
            Encoding.UTF8, "application/json");
        if (host is not null)
        {
            request.Headers.Host = $"{host}:{server.Endpoint.Port}";
        }

        string? origin = originKind switch
        {
            "same" => server.Endpoint.GetLeftPart(UriPartial.Authority),
            "localhost" => $"http://localhost:{server.Endpoint.Port}",
            "wrong-port" => $"http://127.0.0.1:{(server.Endpoint.Port == 65535 ? 65534 : server.Endpoint.Port + 1)}",
            "https://127.0.0.1" => $"https://127.0.0.1:{server.Endpoint.Port}",
            "multiple" => $"{server.Endpoint.GetLeftPart(UriPartial.Authority)}, http://example.test",
            _ => originKind,
        };
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        using HttpResponseMessage response = await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            string payload = await response.Content.ReadAsStringAsync(timeout.Token);
            Assert.Contains("read_file", payload, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    private static Task<McpClient> CreateClientAsync(Uri endpoint, HttpClient http,
        string protocolVersion, CancellationToken cancellationToken) => McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = $"Codex File Inspector HTTP {protocolVersion} test",
            EnableStandaloneGetStream = false,
        }, http),
        new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: cancellationToken);

    private static void AssertSuccess(CallToolResult result)
    {
        Assert.False(result.IsError);
        Assert.Equal("success", result.StructuredContent?.GetProperty("status").GetString());
        AssertCanonicalText(result);
    }

    private static void AssertCanonicalText(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(JsonValueKind.Object, result.StructuredContent.Value.ValueKind);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(result.StructuredContent.Value.GetRawText(), text.Text);
    }
}
