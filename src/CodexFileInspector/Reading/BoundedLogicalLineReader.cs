using System.Buffers;
using System.Text;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;

namespace CodexFileInspector.Reading;

internal readonly record struct BoundedLogicalLine(string Text, long TotalUtf8Bytes);

internal sealed class BoundedLogicalLineReader(Encoding encoding)
{
    private const int ByteBufferSize = 64 * 1024;

    public async ValueTask<bool> ReadAsync(
        Stream stream,
        Func<BoundedLogicalLine, bool> onLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onLine);

        byte[] bytes = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        char[] characters = ArrayPool<char>.Shared.Rent(encoding.GetMaxCharCount(ByteBufferSize));
        Decoder decoder = encoding.GetDecoder();
        LogicalLineParser parser = new(onLine);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int bytesRead = await stream.ReadAsync(bytes.AsMemory(0, ByteBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    if (!FlushDecoder(decoder, characters, parser))
                    {
                        return false;
                    }

                    return parser.Complete();
                }

                int byteOffset = 0;
                while (byteOffset < bytesRead)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    decoder.Convert(
                        bytes,
                        byteOffset,
                        bytesRead - byteOffset,
                        characters,
                        0,
                        characters.Length,
                        flush: false,
                        out int bytesUsed,
                        out int charactersUsed,
                        out _);

                    if (bytesUsed == 0 && charactersUsed == 0)
                    {
                        throw new InvalidOperationException("The text decoder made no progress.");
                    }

                    byteOffset += bytesUsed;
                    if (charactersUsed > 0 && !parser.Process(characters.AsSpan(0, charactersUsed)))
                    {
                        return false;
                    }
                }
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.UnsupportedEncoding,
                "The file contains invalid data for its detected text encoding.",
                innerException: exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(characters);
        }
    }

    private static bool FlushDecoder(
        Decoder decoder,
        char[] characters,
        LogicalLineParser parser)
    {
        while (true)
        {
            decoder.Convert(
                [],
                0,
                0,
                characters,
                0,
                characters.Length,
                flush: true,
                out _,
                out int charactersUsed,
                out bool completed);

            if (charactersUsed > 0 && !parser.Process(characters.AsSpan(0, charactersUsed)))
            {
                return false;
            }

            if (completed)
            {
                return true;
            }
        }
    }

    private sealed class LogicalLineParser(Func<BoundedLogicalLine, bool> onLine)
    {
        private readonly StringBuilder _visiblePrefix = new();
        private long _totalUtf8Bytes;
        private int _visibleUtf8Bytes;
        private char? _pendingHighSurrogate;
        private bool _pendingCarriageReturn;
        private bool _lineHasContent;
        private bool _prefixClosed;

        public bool Process(ReadOnlySpan<char> characters)
        {
            foreach (char character in characters)
            {
                if (!ProcessCharacter(character))
                {
                    return false;
                }
            }

            return true;
        }

        public bool Complete()
        {
            if (_pendingHighSurrogate is not null)
            {
                throw new ToolExecutionException(
                    ToolErrorCodes.UnsupportedEncoding,
                    "The file ends with an incomplete Unicode scalar value.");
            }

            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                return EmitLine();
            }

            return !_lineHasContent || EmitLine();
        }

        private bool ProcessCharacter(char character)
        {
            if (_pendingHighSurrogate is char highSurrogate)
            {
                _pendingHighSurrogate = null;
                if (!char.IsLowSurrogate(character) || !Rune.TryCreate(highSurrogate, character, out Rune rune))
                {
                    throw new ToolExecutionException(
                        ToolErrorCodes.UnsupportedEncoding,
                        "The file contains an invalid Unicode scalar value.");
                }

                return AppendRune(rune);
            }

            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                if (!EmitLine())
                {
                    return false;
                }

                if (character == '\n')
                {
                    return true;
                }
            }

            if (char.IsHighSurrogate(character))
            {
                _pendingHighSurrogate = character;
                return true;
            }

            if (char.IsLowSurrogate(character))
            {
                throw new ToolExecutionException(
                    ToolErrorCodes.UnsupportedEncoding,
                    "The file contains an invalid Unicode scalar value.");
            }

            if (character == '\r')
            {
                _pendingCarriageReturn = true;
                return true;
            }

            if (character == '\n')
            {
                return EmitLine();
            }

            return AppendRune(new Rune(character));
        }

        private bool AppendRune(Rune rune)
        {
            if (rune.Value == 0)
            {
                throw new ToolExecutionException(
                    ToolErrorCodes.BinaryFile,
                    "The file contains NUL data and is treated as binary rather than text.");
            }

            _lineHasContent = true;
            int runeBytes = rune.Utf8SequenceLength;
            _totalUtf8Bytes = checked(_totalUtf8Bytes + runeBytes);
            if (!_prefixClosed && _visibleUtf8Bytes + runeBytes <= ToolBudgets.LineExcerptBytes)
            {
                Span<char> utf16 = stackalloc char[2];
                int written = rune.EncodeToUtf16(utf16);
                _visiblePrefix.Append(utf16[..written]);
                _visibleUtf8Bytes += runeBytes;
            }
            else
            {
                _prefixClosed = true;
            }

            return true;
        }

        private bool EmitLine()
        {
            bool shouldContinue = onLine(new BoundedLogicalLine(_visiblePrefix.ToString(), _totalUtf8Bytes));
            _visiblePrefix.Clear();
            _totalUtf8Bytes = 0;
            _visibleUtf8Bytes = 0;
            _lineHasContent = false;
            _prefixClosed = false;
            return shouldContinue;
        }
    }
}
