using System.Text;
using CodexFileInspector.Contracts;

namespace CodexFileInspector.Searching;

internal readonly record struct GrepExcerpt(string Text, long TotalUtf8Bytes)
{
    public int ReturnedUtf8Bytes => Encoding.UTF8.GetByteCount(Text);

    public bool IsTruncated => ReturnedUtf8Bytes < TotalUtf8Bytes;
}

internal static class GrepExcerptFactory
{
    private const string OmissionMarker = "…";
    private const int OmissionMarkerBytes = 3;

    public static GrepExcerpt Prefix(string line)
    {
        long totalBytes = Encoding.UTF8.GetByteCount(line);
        string text = Utf8Budget.Truncate(line, ToolBudgets.LineExcerptBytes, out _);
        return new GrepExcerpt(text, totalBytes);
    }

    public static GrepExcerpt AroundFirstMatch(string line, int matchStartByte, int matchEndByte)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(line);
        if (utf8.Length <= ToolBudgets.LineExcerptBytes)
        {
            return new GrepExcerpt(line, utf8.Length);
        }

        matchStartByte = Math.Clamp(matchStartByte, 0, utf8.Length);
        matchEndByte = Math.Clamp(matchEndByte, matchStartByte, utf8.Length);
        int sourceBudget = ToolBudgets.LineExcerptBytes - (2 * OmissionMarkerBytes);
        int matchLength = matchEndByte - matchStartByte;
        int retainedMatchLength = Math.Min(matchLength, sourceBudget);
        int remaining = sourceBudget - retainedMatchLength;
        int before = Math.Min(matchStartByte, remaining / 2);
        int windowStart = matchStartByte - before;
        int windowEnd = Math.Min(utf8.Length, matchStartByte + retainedMatchLength + (remaining - before));

        if (windowEnd - windowStart < sourceBudget)
        {
            int shortfall = sourceBudget - (windowEnd - windowStart);
            int additionalBefore = Math.Min(windowStart, shortfall);
            windowStart -= additionalBefore;
            shortfall -= additionalBefore;
            windowEnd = Math.Min(utf8.Length, windowEnd + shortfall);
        }

        windowStart = MoveToScalarStart(utf8, windowStart);
        windowEnd = MoveToScalarStart(utf8, windowEnd);
        if (windowEnd <= windowStart)
        {
            windowStart = MoveToScalarStart(utf8, matchStartByte);
            windowEnd = Math.Min(utf8.Length, windowStart + sourceBudget);
            windowEnd = MoveToScalarStart(utf8, windowEnd);
        }

        string retained = Encoding.UTF8.GetString(utf8.AsSpan(windowStart, windowEnd - windowStart));
        StringBuilder result = new(ToolBudgets.LineExcerptBytes);
        if (windowStart > 0)
        {
            result.Append(OmissionMarker);
        }

        result.Append(retained);
        if (windowEnd < utf8.Length)
        {
            result.Append(OmissionMarker);
        }

        string text = result.ToString();
        if (Encoding.UTF8.GetByteCount(text) > ToolBudgets.LineExcerptBytes)
        {
            text = Utf8Budget.Truncate(text, ToolBudgets.LineExcerptBytes, out _);
        }

        return new GrepExcerpt(text, utf8.Length);
    }

    private static int MoveToScalarStart(ReadOnlySpan<byte> bytes, int offset)
    {
        offset = Math.Clamp(offset, 0, bytes.Length);
        while (offset > 0 && offset < bytes.Length && (bytes[offset] & 0xC0) == 0x80)
        {
            offset--;
        }

        return offset;
    }
}
