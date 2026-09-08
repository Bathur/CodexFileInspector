using System.Text.Json;
using CodexFileInspector.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class StdioServerTests
{
    [Fact]
    public async Task Server_negotiates_and_invokes_all_four_tools_over_stdio()
    {
        using TestWorkspace workspace = new();
        string filePath = workspace.PathFor("sample.txt");
        await File.WriteAllTextAsync(filePath, "one\ntwo");
        string serverExecutable = ResolveServerExecutable();
        List<string> standardError = [];
        StdioClientTransportOptions options = new()
        {
            Name = "Codex File Inspector integration test",
            Command = serverExecutable,
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = standardError.Add,
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using McpClient client = await McpClient.CreateAsync(
            new StdioClientTransport(options),
            cancellationToken: timeout.Token);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(
            ["glob", "grep", "list_directory", "read_file"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        AssertWireMetadata(client, tools);

        CallToolResult read = await client.CallToolAsync(
            "read_file",
            new Dictionary<string, object?> { ["path"] = filePath },
            cancellationToken: timeout.Token);
        Assert.False(read.IsError);
        Assert.Equal("success", read.StructuredContent?.GetProperty("status").GetString());
        Assert.Equal("one\ntwo", read.StructuredContent?.GetProperty("content").GetString());

        CallToolResult list = await client.CallToolAsync(
            "list_directory",
            new Dictionary<string, object?> { ["path"] = workspace.Root },
            cancellationToken: timeout.Token);
        Assert.False(list.IsError);
        Assert.Equal("sample.txt", list.StructuredContent?.GetProperty("entries")[0].GetProperty("name").GetString());

        CallToolResult glob = await client.CallToolAsync(
            "glob",
            new Dictionary<string, object?>
            {
                ["path"] = workspace.Root,
                ["include_globs"] = new[] { "**/*.txt" },
                ["respect_ignore_files"] = false,
            },
            cancellationToken: timeout.Token);
        Assert.False(glob.IsError);
        Assert.Equal(filePath, glob.StructuredContent?.GetProperty("paths")[0].GetString());

        CallToolResult grep = await client.CallToolAsync(
            "grep",
            new Dictionary<string, object?>
            {
                ["path"] = workspace.Root,
                ["pattern"] = "two",
                ["pattern_kind"] = "literal",
                ["respect_ignore_files"] = false,
            },
            cancellationToken: timeout.Token);
        Assert.False(grep.IsError);
        Assert.Equal(1, grep.StructuredContent?.GetProperty("returned_results").GetInt32());

        CallToolResult invalid = await client.CallToolAsync(
            "read_file",
            new Dictionary<string, object?> { ["path"] = "relative.txt" },
            cancellationToken: timeout.Token);
        Assert.True(invalid.IsError);
        Assert.Equal("path_not_absolute", invalid.StructuredContent?.GetProperty("error").GetProperty("code").GetString());
        TextContentBlock invalidText = Assert.IsType<TextContentBlock>(Assert.Single(invalid.Content));
        Assert.Equal(invalid.StructuredContent?.GetRawText(), invalidText.Text);

        CallToolResult missing = await client.CallToolAsync(
            "read_file",
            new Dictionary<string, object?> { ["path"] = workspace.PathFor("missing.txt") },
            cancellationToken: timeout.Token);
        Assert.True(missing.IsError);
        Assert.Equal("path_missing", missing.StructuredContent?.GetProperty("error").GetProperty("code").GetString());
        Assert.False(missing.StructuredContent?.GetProperty("error").GetProperty("retryable").GetBoolean());

        Assert.DoesNotContain(standardError, line => line.Contains("fail", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Server_accepts_an_initialize_era_client()
    {
        string serverExecutable = ResolveServerExecutable();
        StdioClientTransportOptions transportOptions = new()
        {
            Name = "Codex File Inspector legacy integration test",
            Command = serverExecutable,
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };
        McpClientOptions clientOptions = new()
        {
            ProtocolVersion = "2025-06-18",
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using McpClient client = await McpClient.CreateAsync(
            new StdioClientTransport(transportOptions),
            clientOptions,
            cancellationToken: timeout.Token);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        Assert.Equal(
            ["glob", "grep", "list_directory", "read_file"],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        AssertWireMetadata(client, tools);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-06-18")]
    public async Task Root_relative_filters_work_with_an_unrelated_server_working_directory(string? protocolVersion)
    {
        using RootRelativeGlobFixture fixture = new();
        StdioClientTransportOptions transportOptions = new()
        {
            Name = "Codex File Inspector root-relative filter regression",
            Command = ResolveServerExecutable(),
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };
        Assert.NotEqual(fixture.SearchRoot, transportOptions.WorkingDirectory);
        McpClientOptions? clientOptions = protocolVersion is null
            ? null
            : new() { ProtocolVersion = protocolVersion };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using McpClient client = await McpClient.CreateAsync(
            new StdioClientTransport(transportOptions), clientOptions, cancellationToken: timeout.Token);

        foreach (string tool in new[] { "glob", "grep" })
        {
            foreach (bool exclude in new[] { false, true })
            {
                foreach (bool recursive in new[] { false, true })
                {
                    string pattern = recursive ? "**/Character/LyraHeroComponent.cpp" : "Character/LyraHeroComponent.cpp";
                    Dictionary<string, object?> arguments = new()
                    {
                        ["path"] = fixture.SearchRoot,
                        ["include_globs"] = new[] { exclude ? "**/*.cpp" : pattern },
                        ["max_results"] = 5,
                    };
                    if (exclude)
                    {
                        arguments["exclude_globs"] = new[] { pattern };
                    }

                    if (tool == "grep")
                    {
                        arguments["pattern"] = "HandleChangeInitState";
                        arguments["pattern_kind"] = "literal";
                    }

                    CallToolResult result = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
                    Assert.False(result.IsError);
                    Assert.Equal("success", result.StructuredContent?.GetProperty("status").GetString());
                    string[] actual = tool == "glob"
                        ? result.StructuredContent!.Value.GetProperty("paths").EnumerateArray()
                            .Select(path => path.GetString()!).ToArray()
                        : result.StructuredContent!.Value.GetProperty("blocks").EnumerateArray()
                            .Select(block => block.GetProperty("path").GetString()!).ToArray();
                    string[] expected = (exclude, recursive) switch
                    {
                        (false, false) => [fixture.Target],
                        (false, true) => [fixture.Target, fixture.Descendant],
                        (true, false) => [fixture.Keep, fixture.Descendant],
                        (true, true) => [fixture.Keep],
                    };
                    Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
                    Assert.Equal(expected.Length, result.StructuredContent?.GetProperty("total_results").GetInt32());
                }
            }
        }
    }

    private static void AssertWireMetadata(McpClient client, IList<McpClientTool> tools)
    {
        Assert.Equal(ServerInstructions.Text, client.ServerInstructions);
        foreach (Tool expected in ToolContractCatalog.Create().Select(tool => tool.ProtocolTool))
        {
            Tool actual = tools.Single(tool => tool.Name == expected.Name).ProtocolTool;
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Description, actual.Description);
            Assert.True(JsonElement.DeepEquals(expected.InputSchema, actual.InputSchema));
            Assert.NotNull(expected.OutputSchema);
            Assert.NotNull(actual.OutputSchema);
            Assert.True(JsonElement.DeepEquals(expected.OutputSchema.Value, actual.OutputSchema.Value));
            Assert.Equal(expected.Annotations?.ReadOnlyHint, actual.Annotations?.ReadOnlyHint);
            Assert.Equal(expected.Annotations?.DestructiveHint, actual.Annotations?.DestructiveHint);
            Assert.Equal(expected.Annotations?.IdempotentHint, actual.Annotations?.IdempotentHint);
            Assert.Equal(expected.Annotations?.OpenWorldHint, actual.Annotations?.OpenWorldHint);
        }
    }

    private static string ResolveServerExecutable() =>
        Environment.GetEnvironmentVariable("CODEX_FILE_INSPECTOR_SERVER_PATH") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "CodexFileInspector.exe");
}
