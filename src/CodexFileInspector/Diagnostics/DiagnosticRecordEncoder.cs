using System.Buffers;
using System.Text.Json;
using CodexFileInspector.Contracts;

namespace CodexFileInspector.Diagnostics;

internal static class DiagnosticRecordEncoder
{
    public const int MaximumRecordBytes = 256 * 1024;
    private const int DataBudgetBytes = 160 * 1024;
    private const int MaximumExceptionDepth = 4;
    private const string FragmentMarker = "…[truncated]…";
    private static readonly string ServerVersion =
        typeof(DiagnosticRecordEncoder).Assembly.GetName().Version?.ToString() ?? "unknown";
    private static long _sequence;

    public static byte[] Encode(string tool, object arguments, JsonElement result, Exception? exception)
    {
        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer);
        RecordData data = new(writer);
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("time_utc", DateTimeOffset.UtcNow);
        writer.WriteString("server_version", ServerVersion);
        writer.WriteNumber("process_id", Environment.ProcessId);
        writer.WriteNumber("sequence", Interlocked.Increment(ref _sequence));
        writer.WriteString("tool", tool);
        writer.WriteString("status", result.GetProperty("status").GetString());

        // These are already bounded by the canonical 32 KiB tool result. Never
        // copy read content, match blocks, paths, or directory entries here.
        foreach (string property in new[] { "error", "warnings", "warnings_omitted" })
        {
            if (result.TryGetProperty(property, out JsonElement value))
            {
                writer.WritePropertyName(property);
                value.WriteTo(writer);
            }
        }

        string? errorField = null;
        int? errorIndex = null;
        if (result.TryGetProperty("error", out JsonElement error))
        {
            if (error.TryGetProperty("field", out JsonElement field))
            {
                errorField = field.GetString();
            }

            if (error.TryGetProperty("index", out JsonElement index))
            {
                errorIndex = index.GetInt32();
            }
        }

        KeyValuePair<string, object?>[] fields = GetArguments(arguments);
        // Keep the offending array element available even if earlier, enormous
        // elements consume the budget of the normal array representation.
        if (errorIndex is int itemIndex && fields.FirstOrDefault(pair => pair.Key == errorField).Value
            is IReadOnlyList<string> items && itemIndex >= 0 && itemIndex < items.Count)
        {
            writer.WriteStartObject("argument_issue");
            writer.WriteString("field", errorField);
            writer.WriteNumber("index", itemIndex);
            writer.WritePropertyName("value");
            data.WriteText("argument_issue.value", items[itemIndex]);
            writer.WriteEndObject();
        }

        writer.WriteStartObject("arguments");
        foreach ((string name, object? value) in fields.OrderBy(pair => pair.Key == errorField ? 0 : 1))
        {
            writer.WritePropertyName(name);
            data.WriteValue("arguments." + name, value);
        }

        writer.WriteEndObject();
        if (exception is not null)
        {
            writer.WritePropertyName("exception");
            WriteException(data, exception, 0);
        }

        data.WriteTruncations();
        writer.WriteEndObject();
        writer.Flush();
        if (buffer.WrittenCount > MaximumRecordBytes)
        {
            // A future field addition must not bypass the queue's byte bound.
            // The caller isolates this failure from the tool result.
            throw new InvalidOperationException("Diagnostic record exceeded its byte limit.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteException(RecordData data, Exception exception, int depth)
    {
        Utf8JsonWriter writer = data.Writer;
        string field = "exception" + string.Concat(Enumerable.Repeat(".inner", depth));
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        data.WriteText(field + ".type", exception.GetType().FullName);
        writer.WritePropertyName("message");
        data.WriteText(field + ".message", exception.Message);
        writer.WritePropertyName("stack");
        data.WriteText(field + ".stack", exception.StackTrace);
        if (exception.InnerException is { } inner)
        {
            if (depth + 1 < MaximumExceptionDepth)
            {
                writer.WritePropertyName("inner");
                WriteException(data, inner, depth + 1);
            }
            else
            {
                writer.WriteBoolean("inner_omitted", true);
            }
        }

        writer.WriteEndObject();
    }

    private static KeyValuePair<string, object?>[] GetArguments(object arguments) => arguments switch
    {
        ReadFileRequest r =>
        [
            new("path", r.Path), new("start_line", r.StartLine), new("line_count", r.LineCount),
        ],
        ListDirectoryRequest r =>
        [
            new("path", r.Path), new("result_offset", r.ResultOffset), new("max_entries", r.MaxEntries),
        ],
        GlobRequest r =>
        [
            new("path", r.Path), new("include_globs", r.IncludeGlobs), new("exclude_globs", r.ExcludeGlobs),
            new("case_sensitive", r.CaseSensitive), new("include_hidden", r.IncludeHidden),
            new("respect_ignore_files", r.RespectIgnoreFiles), new("result_offset", r.ResultOffset),
            new("max_results", r.MaxResults),
        ],
        GrepRequest r =>
        [
            new("path", r.Path), new("pattern", r.Pattern),
            new("pattern_kind", r.PatternKind is PatternKind.Literal ? "literal" :
                r.PatternKind is PatternKind.Regex ? "regex" : r.PatternKind.ToString()),
            new("case_sensitive", r.CaseSensitive),
            new("output_mode", r.OutputMode switch
            {
                GrepOutputMode.Matches => "matches",
                GrepOutputMode.FilesWithMatches => "files_with_matches",
                GrepOutputMode.Count => "count",
                _ => r.OutputMode.ToString(),
            }),
            new("include_globs", r.IncludeGlobs), new("exclude_globs", r.ExcludeGlobs),
            new("include_hidden", r.IncludeHidden), new("respect_ignore_files", r.RespectIgnoreFiles),
            new("context_lines", r.ContextLines), new("result_offset", r.ResultOffset), new("max_results", r.MaxResults),
        ],
        _ => throw new ArgumentException("Unknown diagnostic argument type.", nameof(arguments)),
    };

    private sealed class RecordData(Utf8JsonWriter writer)
    {
        private readonly List<Truncation> _truncations = [];
        private int _remainingBytes = DataBudgetBytes;
        public Utf8JsonWriter Writer { get; } = writer;

        public void WriteValue(string field, object? value)
        {
            switch (value)
            {
                case null:
                    Writer.WriteNullValue();
                    break;
                case string text:
                    WriteText(field, text);
                    break;
                case int number:
                    Writer.WriteNumberValue(number);
                    break;
                case bool boolean:
                    Writer.WriteBooleanValue(boolean);
                    break;
                case IReadOnlyList<string> items:
                    Writer.WriteStartArray();
                    int retained = Math.Min(items.Count, ToolBudgets.GlobCount);
                    for (int index = 0; index < retained; index++)
                    {
                        WriteText($"{field}[{index}]", items[index]);
                    }

                    Writer.WriteEndArray();
                    if (retained < items.Count)
                    {
                        _truncations.Add(new Truncation(field, "array_items", items.Count, retained, 0));
                    }

                    break;
                default:
                    throw new ArgumentException("Unknown diagnostic value type.", nameof(value));
            }
        }

        public void WriteText(string field, string? text)
        {
            if (text is null)
            {
                Writer.WriteNullValue();
                return;
            }

            int available = Math.Max(0, _remainingBytes - 2);
            // Never encode the entire untrusted input merely to discover that
            // it is too large. Even the initial probe is bounded by the budget.
            if (text.Length <= available)
            {
                JsonEncodedText full = JsonEncodedText.Encode(text);
                if (full.EncodedUtf8Bytes.Length <= available)
                {
                    WriteEncoded(full);
                    return;
                }
            }

            JsonEncodedText best = JsonEncodedText.Encode(string.Empty);
            int bestPrefix = 0;
            int bestSuffix = 0;
            int low = 0;
            int high = Math.Min(text.Length, available);
            while (low <= high)
            {
                int retained = low + ((high - low) / 2);
                int prefix = (retained + 1) / 2;
                int suffix = retained / 2;
                if (prefix > 0 && prefix < text.Length && char.IsHighSurrogate(text[prefix - 1])
                    && char.IsLowSurrogate(text[prefix]))
                {
                    prefix--;
                }

                int suffixStart = text.Length - suffix;
                if (suffix > 0 && suffixStart > 0 && char.IsLowSurrogate(text[suffixStart])
                    && char.IsHighSurrogate(text[suffixStart - 1]))
                {
                    suffix--;
                }

                string fragment = string.Concat(text.AsSpan(0, prefix), FragmentMarker, text.AsSpan(text.Length - suffix));
                JsonEncodedText encoded = JsonEncodedText.Encode(fragment);
                if (encoded.EncodedUtf8Bytes.Length <= available)
                {
                    best = encoded;
                    bestPrefix = prefix;
                    bestSuffix = suffix;
                    low = retained + 1;
                }
                else
                {
                    high = retained - 1;
                }
            }

            WriteEncoded(best);
            _truncations.Add(new Truncation(field, "utf16_characters", text.Length, bestPrefix, bestSuffix));
        }

        private void WriteEncoded(JsonEncodedText text)
        {
            Writer.WriteStringValue(text);
            _remainingBytes = Math.Max(0, _remainingBytes - text.EncodedUtf8Bytes.Length - 2);
        }

        public void WriteTruncations()
        {
            Writer.WriteStartArray("truncations");
            foreach (Truncation item in _truncations)
            {
                Writer.WriteStartObject();
                Writer.WriteString("field", item.Field);
                Writer.WriteString("unit", item.Unit);
                Writer.WriteNumber("original_length", item.OriginalLength);
                Writer.WriteNumber("retained_prefix_length", item.PrefixLength);
                Writer.WriteNumber("retained_suffix_length", item.SuffixLength);
                Writer.WriteEndObject();
            }

            Writer.WriteEndArray();
        }
    }

    private sealed record Truncation(string Field, string Unit, int OriginalLength, int PrefixLength, int SuffixLength);
}
