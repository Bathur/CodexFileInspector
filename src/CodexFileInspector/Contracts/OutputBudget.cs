using System.Text.Json;
using CodexFileInspector.Errors;

namespace CodexFileInspector.Contracts;

internal static class OutputBudget
{
    public static void EnsurePathFits(string path)
    {
        int byteCount = JsonSerializer.SerializeToUtf8Bytes(path).Length;
        if (byteCount > ToolBudgets.PathRecordBytes)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.OutputRecordTooLarge,
                "The serialized path exceeds the fixed output-record budget.",
                path: null,
                limit: ToolBudgets.PathRecordBytes,
                actual: byteCount);
        }
    }

    public static bool EntryFits(DirectoryEntry entry, out int byteCount)
    {
        byteCount = JsonSerializer.SerializeToUtf8Bytes(entry, ToolJsonContext.Default.DirectoryEntry).Length;
        return byteCount <= ToolBudgets.PathRecordBytes;
    }
}
