using System.Text;
using CodexFileInspector.Errors;

namespace CodexFileInspector.Reading;

internal readonly record struct DetectedTextEncoding(Encoding Encoding, string Name);

internal static class TextEncodingDetector
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    public static async ValueTask<DetectedTextEncoding> DetectAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("Text encoding detection requires a seekable stream.", nameof(stream));
        }

        byte[] prefix = new byte[4];
        int length = 0;
        while (length < prefix.Length)
        {
            int read = await stream.ReadAsync(prefix.AsMemory(length, prefix.Length - length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        DetectedTextEncoding detected;
        int preambleLength;
        if (length >= 4 &&
            ((prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00) ||
             (prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)))
        {
            throw Unsupported("UTF-32 is not a supported text encoding.");
        }
        else if (length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            detected = new DetectedTextEncoding(StrictUtf8, "utf-8");
            preambleLength = 3;
        }
        else if (length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            detected = new DetectedTextEncoding(StrictUtf16LittleEndian, "utf-16le");
            preambleLength = 2;
        }
        else if (length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            detected = new DetectedTextEncoding(StrictUtf16BigEndian, "utf-16be");
            preambleLength = 2;
        }
        else
        {
            if (LooksLikeBomlessUtf16(prefix.AsSpan(0, length)))
            {
                throw Unsupported("BOM-less UTF-16 is not a supported text encoding.");
            }

            detected = new DetectedTextEncoding(StrictUtf8, "utf-8");
            preambleLength = 0;
        }

        stream.Seek(preambleLength, SeekOrigin.Begin);
        return detected;
    }

    private static bool LooksLikeBomlessUtf16(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 4)
        {
            return false;
        }

        bool littleEndianAscii = prefix[1] == 0 && prefix[3] == 0 && (prefix[0] != 0 || prefix[2] != 0);
        bool bigEndianAscii = prefix[0] == 0 && prefix[2] == 0 && (prefix[1] != 0 || prefix[3] != 0);
        return littleEndianAscii || bigEndianAscii;
    }

    private static ToolExecutionException Unsupported(string message) =>
        new(ToolErrorCodes.UnsupportedEncoding, message);
}
