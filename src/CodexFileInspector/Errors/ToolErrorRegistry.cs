using System.Collections.Frozen;

namespace CodexFileInspector.Errors;

internal sealed record ToolErrorDefinition(string Code, bool Retryable, string Meaning);

internal static class ToolErrorCodes
{
    public const string AccessDenied = "access_denied";
    public const string BinaryFile = "binary_file";
    public const string ChangedDuringRead = "changed_during_read";
    public const string InvalidArgument = "invalid_argument";
    public const string InvalidPath = "invalid_path";
    public const string InvalidPattern = "invalid_pattern";
    public const string InternalError = "internal_error";
    public const string IoError = "io_error";
    public const string OutputRecordTooLarge = "output_record_too_large";
    public const string PathMissing = "path_missing";
    public const string PathNotAbsolute = "path_not_absolute";
    public const string PathNotDirectory = "path_not_directory";
    public const string PathNotFile = "path_not_file";
    public const string RangePastEof = "range_past_eof";
    public const string ResultOffsetPastEnd = "result_offset_past_end";
    public const string RipgrepEventTooLarge = "ripgrep_event_too_large";
    public const string RipgrepFailed = "ripgrep_failed";
    public const string ScanLimitExceeded = "scan_limit_exceeded";
    public const string SharingViolation = "sharing_violation";
    public const string UnsupportedEncoding = "unsupported_encoding";
}

internal static class ToolErrorRegistry
{
    public static readonly FrozenDictionary<string, ToolErrorDefinition> All = new[]
    {
        Define(ToolErrorCodes.AccessDenied, false, "The operating system denied access."),
        Define(ToolErrorCodes.BinaryFile, false, "The named file is binary or media rather than supported text."),
        Define(ToolErrorCodes.ChangedDuringRead, true, "The file changed materially during this read."),
        Define(ToolErrorCodes.InvalidArgument, false, "One or more tool arguments violate the contract."),
        Define(ToolErrorCodes.InvalidPath, false, "The path is malformed or uses a rejected device namespace."),
        Define(ToolErrorCodes.InvalidPattern, false, "A regular expression or glob pattern is invalid."),
        Define(ToolErrorCodes.InternalError, true, "The Server encountered an unexpected internal failure."),
        Define(ToolErrorCodes.IoError, true, "The operating system reported another input/output failure."),
        Define(ToolErrorCodes.OutputRecordTooLarge, false, "One indivisible output record exceeds its fixed budget."),
        Define(ToolErrorCodes.PathMissing, false, "The named filesystem path does not exist."),
        Define(ToolErrorCodes.PathNotAbsolute, false, "The path is not a fully qualified Windows absolute path."),
        Define(ToolErrorCodes.PathNotDirectory, false, "The named path is not a directory."),
        Define(ToolErrorCodes.PathNotFile, false, "The named path is not a file."),
        Define(ToolErrorCodes.RangePastEof, false, "The requested start line is beyond the file's end."),
        Define(ToolErrorCodes.ResultOffsetPastEnd, false, "The requested positive result offset is past the complete result set."),
        Define(ToolErrorCodes.RipgrepEventTooLarge, false, "A ripgrep JSON event exceeded the fixed parser budget."),
        Define(ToolErrorCodes.RipgrepFailed, true, "The bundled ripgrep process failed unexpectedly."),
        Define(ToolErrorCodes.ScanLimitExceeded, false, "Enumeration exceeded the fixed scan bound."),
        Define(ToolErrorCodes.SharingViolation, true, "Another process temporarily prevented filesystem access."),
        Define(ToolErrorCodes.UnsupportedEncoding, false, "The file does not use a supported deterministic text encoding."),
    }.ToFrozenDictionary(static definition => definition.Code, StringComparer.Ordinal);

    private static ToolErrorDefinition Define(string code, bool retryable, string meaning) =>
        new(code, retryable, meaning);
}
