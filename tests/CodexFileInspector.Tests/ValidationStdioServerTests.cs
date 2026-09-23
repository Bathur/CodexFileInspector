using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ValidationStdioServerTests
{
    [Theory]
    [InlineData("pattern_kind", "grep", "invalid_argument")]
    [InlineData("output_mode", "grep", "invalid_argument")]
    [InlineData("include_globs", "glob", "invalid_argument")]
    [InlineData("path", "read_file", "invalid_path")]
    public async Task Invalid_wire_arguments_return_non_retryable_typed_errors(
        string field,
        string tool,
        string expectedCode)
    {
        using TestWorkspace workspace = new();
        string filePath = workspace.PathFor("sample.txt");
        await File.WriteAllTextAsync(filePath, "MATCH\n");
        StdioClientTransportOptions transportOptions = new()
        {
            Name = "Codex File Inspector input validation regression",
            Command = ResolveServerExecutable(),
            WorkingDirectory = workspace.Root,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using McpClient client = await McpClient.CreateAsync(
            new StdioClientTransport(transportOptions), cancellationToken: timeout.Token);

        Dictionary<string, object?> arguments = new()
        {
            ["path"] = tool == "glob" ? workspace.Root : filePath,
        };
        if (tool == "grep")
        {
            arguments["pattern"] = "MATCH";
            arguments["pattern_kind"] = "literal";
            arguments["output_mode"] = "matches";
        }
        else if (tool == "glob")
        {
            arguments["include_globs"] = new[] { "*.txt" };
            arguments["respect_ignore_files"] = false;
        }

        CallToolResult valid = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
        Assert.False(valid.IsError);
        Assert.Equal("success", valid.StructuredContent?.GetProperty("status").GetString());
        if (tool == "read_file")
        {
            Assert.Equal("MATCH", valid.StructuredContent?.GetProperty("content").GetString());
        }
        else
        {
            Assert.Equal(1, valid.StructuredContent?.GetProperty("returned_results").GetInt32());
        }

        // Keep these values as raw wire arguments, rather than pre-validating
        // or converting them to the Server's enum/request types in the test.
        arguments[field] = field switch
        {
            "pattern_kind" or "output_mode" => 999,
            "include_globs" => null,
            "path" => workspace.PathFor("bad\u0001name.txt"),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        CallToolResult invalid = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
        Assert.True(invalid.IsError);
        Assert.NotNull(invalid.StructuredContent);
        JsonElement output = invalid.StructuredContent.Value;
        Assert.Equal("error", output.GetProperty("status").GetString());
        JsonElement error = output.GetProperty("error");
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Equal(field, error.GetProperty("field").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(invalid.Content));
        Assert.Equal(output.GetRawText(), text.Text);
    }

    private static string ResolveServerExecutable() =>
        Environment.GetEnvironmentVariable("CODEX_FILE_INSPECTOR_SERVER_PATH") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "CodexFileInspector.exe");
}
