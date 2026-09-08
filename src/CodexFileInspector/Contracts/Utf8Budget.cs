using System.Text;

namespace CodexFileInspector.Contracts;

internal static class Utf8Budget
{
    public static string Truncate(string value, int maximumBytes, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);

        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            truncated = false;
            return value;
        }

        StringBuilder builder = new(value.Length);
        int usedBytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (usedBytes + runeBytes > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            usedBytes += runeBytes;
        }

        truncated = true;
        return builder.ToString();
    }
}
