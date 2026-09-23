using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class DiagnosticsStdioServerTests
{
    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2026-07-28")]
    public async Task Diagnostics_are_opt_in_and_record_only_non_success_without_changing_wire_results(string protocolVersion)
    {
        using IsolatedServer disabled = new();
        using IsolatedServer enabled = new();
        string inputDirectory = disabled.Workspace.PathFor("input");
        Directory.CreateDirectory(inputDirectory);
        string textPath = Path.Combine(inputDirectory, "sample.txt");
        await File.WriteAllTextAsync(textPath, "one\ntwo\n");
        string binaryPath = Path.Combine(inputDirectory, "binary.dat");
        await File.WriteAllBytesAsync(binaryPath, Encoding.UTF8.GetBytes(
            "MATCH\n" + new string('x', 128 * 1024) + "\n\0tail\n"));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        await using McpClient baseline = await disabled.StartAsync(diagnostics: false, timeout.Token, protocolVersion);
        await using McpClient diagnostic = await enabled.StartAsync(diagnostics: true, timeout.Token, protocolVersion);

        ToolCall[] successfulCalls =
        [
            new("read_file", new() { ["path"] = textPath }, "success"),
            new("list_directory", new() { ["path"] = inputDirectory }, "success"),
            new("glob", new()
            {
                ["path"] = inputDirectory,
                ["include_globs"] = new[] { "*.txt" },
                ["respect_ignore_files"] = false,
            }, "success"),
            new("grep", new()
            {
                ["path"] = textPath,
                ["pattern"] = "two",
                ["pattern_kind"] = "literal",
            }, "success"),
        ];

        foreach (ToolCall call in successfulCalls)
        {
            await AssertSameWireResultAsync(baseline, diagnostic, call, timeout.Token);
        }

        Assert.False(Directory.Exists(disabled.LogDirectory));
        Assert.False(Directory.Exists(enabled.LogDirectory));

        ToolCall[] recordedCalls =
        [
            new("read_file", new() { ["path"] = "relative.txt" }, "error", "path_not_absolute"),
            new("list_directory", new() { ["path"] = textPath }, "error", "path_not_directory"),
            new("glob", new()
            {
                ["path"] = textPath,
                ["include_globs"] = new[] { "*.txt" },
            }, "error", "path_not_directory"),
            new("grep", new()
            {
                ["path"] = textPath,
                ["pattern"] = "[",
                ["pattern_kind"] = "regex",
            }, "error", "invalid_pattern"),
            new("grep", new()
            {
                ["path"] = binaryPath,
                ["pattern"] = "MATCH",
                ["pattern_kind"] = "literal",
            }, "partial"),
        ];

        List<JsonElement> wireResults = [];
        foreach (ToolCall call in recordedCalls)
        {
            wireResults.Add(await AssertSameWireResultAsync(baseline, diagnostic, call, timeout.Token));
        }

        IReadOnlyList<JsonElement> records = await ReadRecordsAsync(
            enabled.LogDirectory, recordedCalls.Length, timeout.Token);
        Assert.Equal(recordedCalls.Length, records.Count);
        for (int index = 0; index < recordedCalls.Length; index++)
        {
            ToolCall call = recordedCalls[index];
            JsonElement record = records[index];
            Assert.Equal(call.Tool, record.GetProperty("tool").GetString());
            Assert.Equal(call.Status, record.GetProperty("status").GetString());
            foreach ((string name, object? value) in call.Arguments)
            {
                Assert.True(JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(value),
                    record.GetProperty("arguments").GetProperty(name)));
            }

            string detail = call.Status == "error" ? "error" : "warnings";
            Assert.True(JsonElement.DeepEquals(wireResults[index].GetProperty(detail), record.GetProperty(detail)));
        }

        // Read once more while the clients are alive: no exit-time queue drain is assumed.
        // The final partial record also acts as a barrier for all earlier successful calls.
        Assert.Equal(recordedCalls.Length, ReadCompleteRecords(enabled.LogDirectory).Count);
        Assert.False(Directory.Exists(disabled.LogDirectory));
        Assert.False(Directory.Exists(Path.Combine(enabled.WorkingDirectory, "logs")));
        Assert.False(Directory.Exists(Path.Combine(inputDirectory, "logs")));
    }

    [Fact]
    public async Task Unusable_log_directory_does_not_break_non_success_or_later_successful_calls()
    {
        using IsolatedServer server = new();
        await File.WriteAllTextAsync(server.LogDirectory, "This file deliberately prevents creating the logs directory.");
        string textPath = server.Workspace.PathFor("sample.txt");
        await File.WriteAllTextAsync(textPath, "still readable");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await using McpClient client = await server.StartAsync(diagnostics: true, timeout.Token);

        for (int index = 0; index < 3; index++)
        {
            CallToolResult error = await client.CallToolAsync("read_file",
                new Dictionary<string, object?> { ["path"] = "relative.txt" }, cancellationToken: timeout.Token);
            Assert.True(error.IsError);
            Assert.Equal("path_not_absolute", error.StructuredContent?.GetProperty("error").GetProperty("code").GetString());

            CallToolResult success = await client.CallToolAsync("read_file",
                new Dictionary<string, object?> { ["path"] = textPath }, cancellationToken: timeout.Token);
            Assert.False(success.IsError);
            Assert.Equal("success", success.StructuredContent?.GetProperty("status").GetString());
            Assert.Equal("still readable", success.StructuredContent?.GetProperty("content").GetString());
        }

        Assert.True(File.Exists(server.LogDirectory));
        Assert.False(Directory.Exists(server.LogDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version_with_diagnostics_in_either_order_exits_without_creating_logs(bool diagnosticsFirst)
    {
        using IsolatedServer server = new();
        ProcessStartInfo startInfo = new(server.Executable)
        {
            WorkingDirectory = server.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        startInfo.ArgumentList.Add(diagnosticsFirst ? "--diagnostics" : "--version");
        startInfo.ArgumentList.Add(diagnosticsFirst ? "--version" : "--diagnostics");
        using Process process = new() { StartInfo = startInfo };
        Assert.True(process.Start());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.StartsWith("Codex File Inspector ", await output);
            Assert.Equal(string.Empty, await error);
            Assert.False(Directory.Exists(server.LogDirectory));
            Assert.False(Directory.Exists(Path.Combine(server.WorkingDirectory, "logs")));
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

    private static async Task<JsonElement> AssertSameWireResultAsync(
        McpClient baseline,
        McpClient diagnostic,
        ToolCall call,
        CancellationToken cancellationToken)
    {
        CallToolResult expected = await baseline.CallToolAsync(call.Tool, call.Arguments, cancellationToken: cancellationToken);
        CallToolResult actual = await diagnostic.CallToolAsync(call.Tool, call.Arguments, cancellationToken: cancellationToken);
        Assert.Equal(call.Status == "error", actual.IsError);
        Assert.Equal(expected.IsError, actual.IsError);
        Assert.NotNull(expected.StructuredContent);
        Assert.NotNull(actual.StructuredContent);
        JsonElement content = actual.StructuredContent.Value;
        Assert.Equal(call.Status, content.GetProperty("status").GetString());
        Assert.True(JsonElement.DeepEquals(expected.StructuredContent.Value, content));
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(actual.Content));
        Assert.Equal(content.GetRawText(), text.Text);
        if (call.ErrorCode is not null)
        {
            Assert.Equal(call.ErrorCode, content.GetProperty("error").GetProperty("code").GetString());
        }

        if (call.Status == "partial")
        {
            Assert.Contains(content.GetProperty("warnings").EnumerateArray(), warning =>
                warning.GetProperty("code").GetString() == "text_anomaly");
        }

        return content.Clone();
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadRecordsAsync(
        string logDirectory,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            IReadOnlyList<JsonElement> records = ReadCompleteRecords(logDirectory);
            if (records.Count >= expectedCount)
            {
                return records;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static IReadOnlyList<JsonElement> ReadCompleteRecords(string logDirectory)
    {
        List<JsonElement> records = [];
        if (!Directory.Exists(logDirectory))
        {
            return records;
        }

        foreach (string file in Directory.EnumerateFiles(logDirectory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            // A writer may currently be appending the final line; only consume complete JSONL records.
            int finalNewline = text.LastIndexOf('\n');
            if (finalNewline < 0)
            {
                continue;
            }

            foreach (string line in text[..finalNewline].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }
        }

        return records;
    }

    private sealed record ToolCall(
        string Tool,
        Dictionary<string, object?> Arguments,
        string Status,
        string? ErrorCode = null);

    private sealed class IsolatedServer : IDisposable
    {
        private readonly ConcurrentQueue<string> _standardError = new();
        private Process? _process;

        public IsolatedServer()
        {
            Workspace = new TestWorkspace();
            string sourceExecutable = Environment.GetEnvironmentVariable("CODEX_FILE_INSPECTOR_SERVER_PATH") is { Length: > 0 } configured
                ? Path.GetFullPath(configured)
                : Path.Combine(AppContext.BaseDirectory, "CodexFileInspector.exe");
            string sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
            string appDirectory = Workspace.PathFor("app");
            CopyDirectory(sourceDirectory, appDirectory);
            Executable = Path.Combine(appDirectory, Path.GetFileName(sourceExecutable));
            LogDirectory = Path.Combine(appDirectory, "logs");
            WorkingDirectory = Workspace.PathFor("unrelated-cwd");
            Directory.CreateDirectory(WorkingDirectory);
        }

        public TestWorkspace Workspace { get; }
        public string Executable { get; }
        public string LogDirectory { get; }
        public string WorkingDirectory { get; }

        public async Task<McpClient> StartAsync(bool diagnostics, CancellationToken cancellationToken, string? protocolVersion = null)
        {
            StdioClientTransportOptions options = new()
            {
                Name = "Codex File Inspector isolated diagnostics integration test",
                Command = Executable,
                Arguments = diagnostics ? ["--diagnostics"] : [],
                WorkingDirectory = WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables(),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = _standardError.Enqueue,
            };
            McpClientOptions? clientOptions = protocolVersion is null ? null : new() { ProtocolVersion = protocolVersion };
            McpClient client = await McpClient.CreateAsync(
                new StdioClientTransport(options), clientOptions, cancellationToken: cancellationToken);
            try
            {
                _process = FindServerProcess();
                // Retain an OS process handle before client disposal, so cleanup
                // waits for this precise child rather than a possibly reused PID.
                _ = _process.Handle;
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public void Dispose()
        {
            bool exited = true;
            if (_process is not null)
            {
                try
                {
                    // MCP transport disposal can finish before the operating
                    // system releases the apphost image. Wait for the actual
                    // child exit before deleting its isolated installation.
                    exited = _process.WaitForExit(5_000);
                    if (!exited)
                    {
                        _process.Kill(entireProcessTree: true);
                        Assert.True(_process.WaitForExit(5_000), "The isolated server child could not be terminated.");
                    }
                }
                finally
                {
                    _process.Dispose();
                }
            }

            Workspace.Dispose();
            Assert.True(exited, "The isolated server did not exit after MCP client disposal. stderr:\n" +
                string.Join('\n', _standardError));
        }

        private Process FindServerProcess()
        {
            Process? match = null;
            foreach (Process candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
            {
                bool retain = false;
                try
                {
                    if (string.Equals(candidate.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Null(match);
                        match = candidate;
                        retain = true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Another server with the same executable name may have exited.
                }
                catch (Win32Exception)
                {
                    // An unrelated server process need not permit module inspection.
                }
                finally
                {
                    if (!retain)
                    {
                        candidate.Dispose();
                    }
                }
            }

            return match ?? throw new InvalidOperationException(
                "Could not identify the isolated MCP server process. stderr:\n" + string.Join('\n', _standardError));
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (string directory in Directory.EnumerateDirectories(source))
            {
                if (!string.Equals(Path.GetFileName(directory), "logs", StringComparison.OrdinalIgnoreCase))
                {
                    CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
                }
            }
        }
    }
}
