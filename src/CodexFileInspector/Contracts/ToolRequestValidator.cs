using System.Text;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;

namespace CodexFileInspector.Contracts;

internal sealed class ToolValidationException(
    string code,
    string message,
    string? field = null,
    int? index = null,
    long? limit = null,
    long? actual = null) : ArgumentException(message)
{
    public string Code { get; } = code;

    public string? Field { get; } = field;

    public int? Index { get; } = index;

    public long? Limit { get; } = limit;

    public long? Actual { get; } = actual;
}

internal sealed class ToolRequestValidator(IFileSystemPlatform fileSystem)
{
    public ReadFileRequest Validate(ReadFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInclusiveRange(request.StartLine, 1, ToolBudgets.ReadFileStartLineMaximum, "start_line");
        ValidateInclusiveRange(request.LineCount, 1, ToolBudgets.ReadFileLineCountMaximum, "line_count");
        return request with { Path = fileSystem.NormalizeAbsolutePath(request.Path) };
    }

    public GrepRequest Validate(GrepRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PatternKind is not (PatternKind.Literal or PatternKind.Regex))
        {
            throw Invalid("pattern_kind", "pattern_kind must be literal or regex.");
        }

        if (request.OutputMode is not (GrepOutputMode.Matches or GrepOutputMode.FilesWithMatches or GrepOutputMode.Count))
        {
            throw Invalid("output_mode", "output_mode must be matches, files_with_matches, or count.");
        }

        if (string.IsNullOrEmpty(request.Pattern) ||
            request.Pattern.ContainsAny('\r', '\n') ||
            request.Pattern.Contains('\0'))
        {
            throw Invalid("pattern", "pattern must be non-empty and contain exactly one logical line.");
        }

        ValidateUtf8Length(request.Pattern, ToolBudgets.GrepPatternBytes, "pattern");
        ValidateInclusiveRange(request.ContextLines, 0, ToolBudgets.GrepContextLinesMaximum, "context_lines");
        ValidateInclusiveRange(request.ResultOffset, 0, ToolBudgets.GrepResultOffsetMaximum, "result_offset");
        ValidateInclusiveRange(request.MaxResults, 1, ToolBudgets.GrepMaximumResults, "max_results");
        if (request.ContextLines > 0 && request.OutputMode is not GrepOutputMode.Matches)
        {
            throw Invalid("context_lines", "context_lines is valid only when output_mode is matches.");
        }

        ValidateGlobSet(request.IncludeGlobs ?? [], request.ExcludeGlobs ?? [], requireInclude: false);
        return request with { Path = fileSystem.NormalizeAbsolutePath(request.Path) };
    }

    public GlobRequest Validate(GlobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IncludeGlobs is null)
        {
            throw Invalid("include_globs", "include_globs must contain at least one pattern.");
        }

        ValidateInclusiveRange(request.ResultOffset, 0, ToolBudgets.SearchResultOffsetMaximum, "result_offset");
        ValidateInclusiveRange(request.MaxResults, 1, ToolBudgets.GlobMaximumResults, "max_results");
        ValidateGlobSet(request.IncludeGlobs, request.ExcludeGlobs ?? [], requireInclude: true);
        return request with { Path = fileSystem.NormalizeAbsolutePath(request.Path) };
    }

    public ListDirectoryRequest Validate(ListDirectoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInclusiveRange(request.ResultOffset, 0, ToolBudgets.ListDirectoryOffsetPlusEntriesMaximum - 1, "result_offset");
        ValidateInclusiveRange(request.MaxEntries, 1, ToolBudgets.ListDirectoryEntryMaximum, "max_entries");

        long combined = (long)request.ResultOffset + request.MaxEntries;
        if (combined > ToolBudgets.ListDirectoryOffsetPlusEntriesMaximum)
        {
            throw new ToolValidationException(
                ToolErrorCodes.InvalidArgument,
                "result_offset plus max_entries exceeds the fixed bound.",
                "max_entries",
                limit: ToolBudgets.ListDirectoryOffsetPlusEntriesMaximum,
                actual: combined);
        }

        return request with { Path = fileSystem.NormalizeAbsolutePath(request.Path) };
    }

    private static void ValidateGlobSet(
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs,
        bool requireInclude)
    {
        if (requireInclude && includeGlobs.Count == 0)
        {
            throw Invalid("include_globs", "include_globs must contain at least one pattern.");
        }

        int count = includeGlobs.Count + excludeGlobs.Count;
        if (count > ToolBudgets.GlobCount)
        {
            throw new ToolValidationException(
                ToolErrorCodes.InvalidArgument,
                "The combined include and exclude glob count exceeds the fixed bound.",
                "include_globs",
                limit: ToolBudgets.GlobCount,
                actual: count);
        }

        int totalBytes = 0;
        ValidateGlobArray(includeGlobs, "include_globs", ref totalBytes);
        ValidateGlobArray(excludeGlobs, "exclude_globs", ref totalBytes);
        if (totalBytes > ToolBudgets.GlobTotalBytes)
        {
            throw new ToolValidationException(
                ToolErrorCodes.InvalidArgument,
                "The combined UTF-8 size of include and exclude globs exceeds the fixed bound.",
                "include_globs",
                limit: ToolBudgets.GlobTotalBytes,
                actual: totalBytes);
        }
    }

    private static void ValidateGlobArray(IReadOnlyList<string> globs, string field, ref int totalBytes)
    {
        for (int index = 0; index < globs.Count; index++)
        {
            string glob = globs[index];
            if (string.IsNullOrEmpty(glob))
            {
                throw InvalidPattern(field, index, "Glob patterns must be non-empty.");
            }

            int bytes = Encoding.UTF8.GetByteCount(glob);
            if (bytes > ToolBudgets.GlobBytes)
            {
                throw new ToolValidationException(
                    ToolErrorCodes.InvalidPattern,
                    "A glob pattern exceeds the fixed UTF-8 size bound.",
                    field,
                    index,
                    ToolBudgets.GlobBytes,
                    bytes);
            }

            totalBytes = checked(totalBytes + bytes);
            if (glob[0] is '!' || glob.Contains('\\') || glob.StartsWith('/') ||
                (glob.Length >= 2 && char.IsAsciiLetter(glob[0]) && glob[1] is ':') ||
                glob.Contains('\0') ||
                glob.Split('/').Any(static segment => segment is ".."))
            {
                throw InvalidPattern(
                    field,
                    index,
                    "Globs must be relative, use '/', avoid '..', and must not use a leading '!'.");
            }
        }
    }

    private static void ValidateUtf8Length(string value, int maximum, string field)
    {
        int bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes > maximum)
        {
            throw new ToolValidationException(
                ToolErrorCodes.InvalidArgument,
                $"{field} exceeds the fixed UTF-8 size bound.",
                field,
                limit: maximum,
                actual: bytes);
        }
    }

    private static void ValidateInclusiveRange(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum)
        {
            throw new ToolValidationException(
                ToolErrorCodes.InvalidArgument,
                $"{field} must be between {minimum} and {maximum}.",
                field,
                limit: maximum,
                actual: value);
        }
    }

    private static ToolValidationException Invalid(string field, string message) =>
        new(ToolErrorCodes.InvalidArgument, message, field);

    private static ToolValidationException InvalidPattern(string field, int index, string message) =>
        new(ToolErrorCodes.InvalidPattern, message, field, index);
}

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, char first, char second) =>
        value.Contains(first) || value.Contains(second);
}
