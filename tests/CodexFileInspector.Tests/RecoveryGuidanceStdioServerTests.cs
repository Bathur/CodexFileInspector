using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class RecoveryGuidanceStdioServerTests
{
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task Recovery_guidance_survives_both_stdio_protocols_without_changing_successful_calls(string protocolVersion)
    {
        using TestWorkspace workspace = new();
        string longRoot = ProcessStartGuidanceTests.CreateDirectoryOfLength(workspace.Root, 317);
        string file = Path.Combine(longRoot, "sample.txt");
        await File.WriteAllTextAsync(file, "MATCH");
        string executable = Environment.GetEnvironmentVariable("CODEX_FILE_INSPECTOR_SERVER_PATH") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "CodexFileInspector.exe");
        StdioClientTransportOptions options = new()
        {
            Name = "Codex File Inspector recovery guidance integration test",
            Command = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await using McpClient client = await McpClient.CreateAsync(new StdioClientTransport(options),
            new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: timeout.Token);

        foreach (string tool in new[] { "grep", "glob" })
        {
            Dictionary<string, object?> arguments = new() { ["path"] = longRoot };
            if (tool == "grep")
            {
                arguments["pattern"] = "MATCH";
                arguments["pattern_kind"] = "literal";
            }
            else
            {
                arguments["include_globs"] = new[] { "*.txt" };
            }

            CallToolResult failure = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
            JsonElement error = AssertError(failure, "io_error");
            Assert.Contains("working directory is long", error.GetProperty("message").GetString());
            Assert.Equal(longRoot, error.GetProperty("path").GetString());
            Assert.True(error.TryGetProperty("os_code", out _));
        }

        string shortFile = workspace.PathFor("short.txt");
        await File.WriteAllTextAsync(shortFile, "MATCH");
        foreach ((string pattern, string expectedReason) in new[]
        {
            ("MATCH(?=$)", "lookaround"),
            (@"(MATCH)\1", "backreference"),
        })
        {
            CallToolResult failure = await client.CallToolAsync("grep", new Dictionary<string, object?>
            {
                ["path"] = shortFile, ["pattern"] = pattern, ["pattern_kind"] = "regex",
            }, cancellationToken: timeout.Token);
            JsonElement error = AssertError(failure, "invalid_pattern");
            Assert.Contains(expectedReason, error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("pattern", error.GetProperty("field").GetString());
        }

        CallToolResult read = await client.CallToolAsync("read_file", new Dictionary<string, object?>
        {
            ["path"] = file,
        }, cancellationToken: timeout.Token);
        Assert.False(read.IsError);
        Assert.Equal("MATCH", read.StructuredContent?.GetProperty("content").GetString());
    }

    private static JsonElement AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        JsonElement body = result.StructuredContent.Value;
        Assert.Equal("error", body.GetProperty("status").GetString());
        Assert.Equal(body.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        JsonElement error = body.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        return error;
    }
}
