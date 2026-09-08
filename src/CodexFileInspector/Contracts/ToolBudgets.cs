namespace CodexFileInspector.Contracts;

internal static class ToolBudgets
{
    public const int CanonicalResultBytes = 32 * 1024;
    public const int ErrorMessageBytes = 1024;
    public const int GlobBytes = 1024;
    public const int GlobCount = 64;
    public const int GlobTotalBytes = 16 * 1024;
    public const int GrepContextLinesMaximum = 20;
    public const int GrepMaximumResults = 1000;
    public const int GrepPatternBytes = 8 * 1024;
    public const int GrepResultOffsetMaximum = 1_000_000;
    public const int GrepResultsDefault = 100;
    public const int LineExcerptBytes = 4 * 1024;
    public const int ListDirectoryEntryMaximum = 2000;
    public const int ListDirectoryEntriesDefault = 200;
    public const int ListDirectoryOffsetPlusEntriesMaximum = 100_000;
    public const int ListDirectoryScanMaximum = 1_000_000;
    public const int PathRecordBytes = 16 * 1024;
    public const int ReadFileLineCountDefault = 200;
    public const int ReadFileLineCountMaximum = 2000;
    public const int ReadFileStartLineMaximum = int.MaxValue;
    public const int RipgrepEventBytes = 8 * 1024 * 1024;
    public const int RipgrepCommandLineCharacters = 30_000;
    public const int RipgrepStandardErrorBytes = 64 * 1024;
    public const int SearchResultOffsetMaximum = 1_000_000;
    public const int GlobMaximumResults = 2000;
    public const int GlobResultsDefault = 200;
    public const int WarningCount = 20;
    public const int WarningMessageBytes = 512;
    public const int WarningMessagesTotalBytes = 4 * 1024;
}
