using System.Text.Json.Serialization;

namespace CodexFileInspector.Contracts;

internal abstract record ToolOutput
{
    [JsonPropertyName("status"), JsonPropertyOrder(-100)]
    public required ToolStatus Status { get; init; }

    [JsonPropertyName("warnings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolWarning>? Warnings { get; init; }

    [JsonPropertyName("warnings_omitted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WarningsOmitted { get; init; }

    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolError? Error { get; init; }
}

internal sealed record ReadFileOutput : ToolOutput
{
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("encoding"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Encoding { get; init; }

    [JsonPropertyName("start_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartLine { get; init; }

    [JsonPropertyName("end_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EndLine { get; init; }

    [JsonPropertyName("returned_lines"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReturnedLines { get; init; }

    [JsonPropertyName("total_lines"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalLines { get; init; }

    [JsonPropertyName("has_more"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_start_line"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? NextStartLine { get; init; }

    [JsonPropertyName("stop_reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }

    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("line_truncations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LineTruncation>? LineTruncations { get; init; }
}

internal sealed record GrepOutput : ToolOutput
{
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("blocks"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GrepMatchBlock>? Blocks { get; init; }

    [JsonPropertyName("paths"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Paths { get; init; }

    [JsonPropertyName("counts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GrepFileCount>? Counts { get; init; }

    [JsonPropertyName("totals"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GrepTotals? Totals { get; init; }

    [JsonPropertyName("returned_results"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReturnedResults { get; init; }

    [JsonPropertyName("total_results"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalResults { get; init; }

    [JsonPropertyName("has_more"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_result_offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextResultOffset { get; init; }

    [JsonPropertyName("truncated_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruncatedBy { get; init; }
}

internal sealed record GlobOutput : ToolOutput
{
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("paths"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Paths { get; init; }

    [JsonPropertyName("returned_results"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReturnedResults { get; init; }

    [JsonPropertyName("total_results"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalResults { get; init; }

    [JsonPropertyName("has_more"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_result_offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextResultOffset { get; init; }

    [JsonPropertyName("truncated_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruncatedBy { get; init; }
}

internal sealed record ListDirectoryOutput : ToolOutput
{
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("entries"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DirectoryEntry>? Entries { get; init; }

    [JsonPropertyName("returned_entries"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReturnedEntries { get; init; }

    [JsonPropertyName("total_entries"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalEntries { get; init; }

    [JsonPropertyName("has_more"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_result_offset"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NextResultOffset { get; init; }

    [JsonPropertyName("truncated_by"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TruncatedBy { get; init; }
}
