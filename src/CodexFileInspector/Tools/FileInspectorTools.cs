using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CodexFileInspector.Contracts;
using CodexFileInspector.DirectoryListing;
using CodexFileInspector.Diagnostics;
using CodexFileInspector.Errors;
using CodexFileInspector.Globbing;
using CodexFileInspector.Reading;
using CodexFileInspector.Searching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodexFileInspector.Tools;

[McpServerToolType]
public static class FileInspectorTools
{
    [McpServerTool(
        Name = "read_file",
        Title = "Read text file",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ReadFileOutput))]
    [Description("Read a bounded 1-based line range from one text file. Returns logical text without injected line-number prefixes, plus encoding, explicit truncation, and reusable next_start_line continuation. has_more indicates later lines; line_truncations reports clipped text within returned lines.")]
    public static async ValueTask<CallToolResult> ReadFileAsync(
        [MinLength(1), Description("Fully qualified Windows absolute file path.")]
        string path,
        IServiceProvider services,
        [Range(1, int.MaxValue), Description("First logical line to return, 1-based. Defaults to 1 and must be at most 2147483647.")]
        int start_line = 1,
        [Range(1, ToolBudgets.ReadFileLineCountMaximum), Description("Maximum logical lines to return. Defaults to 200; valid range is 1 through 2000.")]
        int line_count = ToolBudgets.ReadFileLineCountDefault,
        CancellationToken cancellationToken = default)
    {
        ReadFileRequest arguments = new(path, start_line, line_count);
        try
        {
            ToolRequestValidator validator = services.GetRequiredService<ToolRequestValidator>();
            ReadFileService reader = services.GetRequiredService<ReadFileService>();
            ReadFileRequest request = validator.Validate(arguments);
            ReadFileOutput output = await reader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.ReadFileOutput, services, "read_file", arguments);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(services, exception, "read_file");
            ReadFileOutput output = new()
            {
                Status = ToolStatus.Error,
                Error = ToolExceptionMapper.Map(exception, path),
            };
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.ReadFileOutput, services, "read_file", arguments, exception);
        }
    }

    [McpServerTool(
        Name = "list_directory",
        Title = "List directory",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ListDirectoryOutput))]
    [Description("List the direct children of one directory without filtering. Returns each exact child name, normalized absolute path, and file, directory, link, or other kind.")]
    public static CallToolResult ListDirectory(
        [MinLength(1), Description("Fully qualified Windows absolute directory path.")]
        string path,
        IServiceProvider services,
        [Range(0, ToolBudgets.ListDirectoryOffsetPlusEntriesMaximum - 1), Description("Number of stable name-sorted entries to skip. Defaults to 0.")]
        int result_offset = 0,
        [Range(1, ToolBudgets.ListDirectoryEntryMaximum), Description("Maximum entries to return. Defaults to 200; valid range is 1 through 2000, and offset plus maximum must not exceed 100000.")]
        int max_entries = ToolBudgets.ListDirectoryEntriesDefault,
        CancellationToken cancellationToken = default)
    {
        ListDirectoryRequest arguments = new(path, result_offset, max_entries);
        try
        {
            ToolRequestValidator validator = services.GetRequiredService<ToolRequestValidator>();
            ListDirectoryService lister = services.GetRequiredService<ListDirectoryService>();
            ListDirectoryRequest request = validator.Validate(arguments);
            ListDirectoryOutput output = lister.List(request, cancellationToken);
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.ListDirectoryOutput, services, "list_directory", arguments);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(services, exception, "list_directory");
            ListDirectoryOutput output = new()
            {
                Status = ToolStatus.Error,
                Error = ToolExceptionMapper.Map(exception, path),
            };
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.ListDirectoryOutput, services, "list_directory", arguments, exception);
        }
    }

    [McpServerTool(
        Name = "glob",
        Title = "Find files by path pattern",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(GlobOutput))]
    [Description("Recursively find files under one directory using a deduplicated union of glob patterns. Returns only normalized absolute file paths in stable order.")]
    public static async ValueTask<CallToolResult> GlobAsync(
        [MinLength(1), Description("Fully qualified Windows absolute directory path.")]
        string path,
        [MinLength(1), Description("One or more non-empty root-relative '/'-separated include globs. Absolute, '..', backslash, and leading-'!' forms are rejected.")]
        string[] include_globs,
        IServiceProvider services,
        [Description("Optional root-relative '/'-separated file globs to exclude. Excludes have highest priority.")]
        string[]? exclude_globs = null,
        [Description("Whether path glob matching is case-sensitive. Defaults to false on Windows.")]
        bool case_sensitive = false,
        [Description("Include hidden files and directories. Defaults to false; explicit include globs and ignore-file allow rules can override hidden filtering.")]
        bool include_hidden = false,
        [Description("Respect applicable project ignore files while disabling global ignore configuration. Defaults to true.")]
        bool respect_ignore_files = true,
        [Range(0, ToolBudgets.SearchResultOffsetMaximum), Description("Number of stable file results to skip. Defaults to 0; maximum is 1000000.")]
        int result_offset = 0,
        [Range(1, ToolBudgets.GlobMaximumResults), Description("Maximum file paths to return. Defaults to 200; valid range is 1 through 2000.")]
        int max_results = ToolBudgets.GlobResultsDefault,
        CancellationToken cancellationToken = default)
    {
        GlobRequest arguments = new(
            path, include_globs, exclude_globs, case_sensitive, include_hidden,
            respect_ignore_files, result_offset, max_results);
        try
        {
            ToolRequestValidator validator = services.GetRequiredService<ToolRequestValidator>();
            GlobService finder = services.GetRequiredService<GlobService>();
            GlobRequest request = validator.Validate(arguments);
            GlobOutput output = await finder.FindAsync(request, cancellationToken).ConfigureAwait(false);
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.GlobOutput, services, "glob", arguments);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(services, exception, "glob");
            GlobOutput output = new()
            {
                Status = ToolStatus.Error,
                Error = ToolExceptionMapper.Map(exception, path),
            };
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.GlobOutput, services, "glob", arguments, exception);
        }
    }

    [McpServerTool(
        Name = "grep",
        Title = "Search file contents",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(GrepOutput))]
    [Description("Search one file or directory for a literal or regular-expression pattern. Results are stable, bounded, and continuable; matches mode can include symmetric context.")]
    public static async ValueTask<CallToolResult> GrepAsync(
        [MinLength(1), Description("Fully qualified Windows absolute file or directory path.")]
        string path,
        [MinLength(1), Description("Non-empty single-line pattern, at most 8 KiB UTF-8.")]
        string pattern,
        [Description("How to interpret pattern: literal or regex.")]
        PatternKind pattern_kind,
        IServiceProvider services,
        [Description("Whether matching is case-sensitive. Defaults to true; smart-case is not used.")]
        bool case_sensitive = true,
        [Description("Pagination and returned_results/total_results count matching lines in matches and matching files in files_with_matches/count, not context blocks or occurrences. In count mode, totals summarize the full search, not the page.")]
        GrepOutputMode output_mode = GrepOutputMode.Matches,
        [Description("Optional union of root-relative '/'-separated file globs. Explicit includes may override ignore rules and default hidden filtering.")]
        string[]? include_globs = null,
        [Description("Optional root-relative '/'-separated file globs to exclude. Excludes have highest priority.")]
        string[]? exclude_globs = null,
        [Description("Include hidden files and directories. Defaults to false; explicit include globs and ignore-file allow rules can override hidden filtering.")]
        bool include_hidden = false,
        [Description("Respect applicable project ignore files while disabling global ignore configuration. Defaults to true.")]
        bool respect_ignore_files = true,
        [Range(0, ToolBudgets.GrepContextLinesMaximum), Description("Symmetric context lines around selected matching lines. Valid range is 0 through 20 and only valid in matches mode.")]
        int context_lines = 0,
        [Range(0, ToolBudgets.GrepResultOffsetMaximum), Description("Number of stable result units to skip. Defaults to 0; maximum is 1000000.")]
        int result_offset = 0,
        [Range(1, ToolBudgets.GrepMaximumResults), Description("Maximum result units to return. Defaults to 100; valid range is 1 through 1000.")]
        int max_results = ToolBudgets.GrepResultsDefault,
        CancellationToken cancellationToken = default)
    {
        GrepRequest arguments = new(
            path, pattern, pattern_kind, case_sensitive, output_mode, include_globs, exclude_globs,
            include_hidden, respect_ignore_files, context_lines, result_offset, max_results);
        try
        {
            ToolRequestValidator validator = services.GetRequiredService<ToolRequestValidator>();
            GrepService searcher = services.GetRequiredService<GrepService>();
            GrepRequest request = validator.Validate(arguments);
            GrepOutput output = await searcher.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.GrepOutput, services, "grep", arguments);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnexpected(services, exception, "grep");
            GrepOutput output = new()
            {
                Status = ToolStatus.Error,
                Error = ToolExceptionMapper.Map(exception, path),
            };
            return ToolDiagnostics.CreateResult(output, ToolJsonContext.Default.GrepOutput, services, "grep", arguments, exception);
        }
    }

    private static void LogUnexpected(IServiceProvider services, Exception exception, string toolName)
    {
        if (ToolExceptionMapper.IsExpected(exception))
        {
            return;
        }

        ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CodexFileInspector.Tools");
        logger.LogError(exception, "Unexpected failure in {ToolName}.", toolName);
    }
}
