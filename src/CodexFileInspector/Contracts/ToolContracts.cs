using System.Text.Json.Serialization;

namespace CodexFileInspector.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ToolStatus>))]
internal enum ToolStatus
{
    [JsonStringEnumMemberName("success")]
    Success,

    [JsonStringEnumMemberName("partial")]
    Partial,

    [JsonStringEnumMemberName("error")]
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<PatternKind>))]
public enum PatternKind
{
    [JsonStringEnumMemberName("literal")]
    Literal,

    [JsonStringEnumMemberName("regex")]
    Regex,
}

[JsonConverter(typeof(JsonStringEnumConverter<GrepOutputMode>))]
public enum GrepOutputMode
{
    [JsonStringEnumMemberName("matches")]
    Matches,

    [JsonStringEnumMemberName("files_with_matches")]
    FilesWithMatches,

    [JsonStringEnumMemberName("count")]
    Count,
}

[JsonConverter(typeof(JsonStringEnumConverter<DirectoryEntryKind>))]
internal enum DirectoryEntryKind
{
    [JsonStringEnumMemberName("file")]
    File,

    [JsonStringEnumMemberName("directory")]
    Directory,

    [JsonStringEnumMemberName("link")]
    Link,

    [JsonStringEnumMemberName("other")]
    Other,
}

internal sealed record ReadFileRequest(
    string Path,
    int StartLine = 1,
    int LineCount = ToolBudgets.ReadFileLineCountDefault);

internal sealed record GrepRequest(
    string Path,
    string Pattern,
    PatternKind PatternKind,
    bool CaseSensitive = true,
    GrepOutputMode OutputMode = GrepOutputMode.Matches,
    IReadOnlyList<string>? IncludeGlobs = null,
    IReadOnlyList<string>? ExcludeGlobs = null,
    bool IncludeHidden = false,
    bool RespectIgnoreFiles = true,
    int ContextLines = 0,
    int ResultOffset = 0,
    int MaxResults = ToolBudgets.GrepResultsDefault);

internal sealed record GlobRequest(
    string Path,
    IReadOnlyList<string> IncludeGlobs,
    IReadOnlyList<string>? ExcludeGlobs = null,
    bool CaseSensitive = false,
    bool IncludeHidden = false,
    bool RespectIgnoreFiles = true,
    int ResultOffset = 0,
    int MaxResults = ToolBudgets.GlobResultsDefault);

internal sealed record ListDirectoryRequest(
    string Path,
    int ResultOffset = 0,
    int MaxEntries = ToolBudgets.ListDirectoryEntriesDefault);

internal sealed record ToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("field"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null,
    [property: JsonPropertyName("index"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Index = null,
    [property: JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Limit = null,
    [property: JsonPropertyName("actual"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Actual = null,
    [property: JsonPropertyName("total"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Total = null,
    [property: JsonPropertyName("os_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OsCode = null);

internal sealed record ToolWarning(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null);

internal sealed record LineTruncation(
    [property: JsonPropertyName("line_number")] long LineNumber,
    [property: JsonPropertyName("returned_bytes")] int ReturnedBytes,
    [property: JsonPropertyName("total_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalBytes);

internal sealed record DirectoryEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] DirectoryEntryKind Kind);

internal sealed record GrepMatchLine(
    [property: JsonPropertyName("line_number")] long LineNumber,
    [property: JsonPropertyName("occurrence_count")] int OccurrenceCount);

internal sealed record GrepMatchBlock(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("start_line")] long StartLine,
    [property: JsonPropertyName("end_line")] long EndLine,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("match_lines")] IReadOnlyList<GrepMatchLine> MatchLines,
    [property: JsonPropertyName("line_truncations")] IReadOnlyList<LineTruncation> LineTruncations,
    [property: JsonPropertyName("context_truncated")] bool ContextTruncated);

internal sealed record GrepFileCount(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("matching_lines")] long MatchingLines,
    [property: JsonPropertyName("occurrences")] long Occurrences);

internal sealed record GrepTotals(
    [property: JsonPropertyName("matching_files")] long MatchingFiles,
    [property: JsonPropertyName("matching_lines")] long MatchingLines,
    [property: JsonPropertyName("occurrences")] long Occurrences);
