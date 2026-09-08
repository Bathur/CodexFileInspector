using System.Text;
using System.Text.Json;
using CodexFileInspector.Contracts;
using CodexFileInspector.Errors;
using CodexFileInspector.Platform;

namespace CodexFileInspector.Searching;

internal readonly record struct GrepLineKey(string Path, long LineNumber);

internal sealed record GrepLineData(
    string Path,
    long LineNumber,
    GrepExcerpt Excerpt,
    bool IsSelectedMatch,
    int OccurrenceCount);

internal sealed record GrepSelectedMatch(
    string Path,
    long LineNumber,
    GrepExcerpt Excerpt,
    int OccurrenceCount);

internal sealed class GrepJsonAccumulator(
    GrepRequest request,
    string workingDirectory,
    IFileSystemPlatform fileSystem,
    WarningCollector warnings)
{
    private const int RetainedContextBytesMaximum = 2 * 1024 * 1024;
    private const int RetainedContextLinesMaximum = 10_000;

    private readonly Queue<GrepLineData> _priorLines = new();
    private readonly Dictionary<GrepLineKey, GrepLineData> _lines = new();
    private readonly List<GrepSelectedMatch> _selectedMatches = [];
    private readonly List<string> _matchingFiles = [];
    private readonly List<GrepFileCount> _countPage = [];
    private readonly List<long> _selectedLinesInCurrentFile = [];
    private readonly HashSet<GrepLineKey> _preRemovedContext = [];
    private string? _currentLinePath;
    private string? _lastSelectedPath;
    private string? _lastMatchingFilePath;
    private long _lastSelectedLine;
    private long _matchResultsSeen;
    private long _matchingLines;
    private long _occurrences;
    private long _matchingFileCount;
    private bool _extraResultSeen;
    private bool _anomaly;
    private int _retainedContextBytes;
    private int _retainedContextLines;

    public IReadOnlyList<GrepSelectedMatch> SelectedMatches => _selectedMatches;

    public IReadOnlyDictionary<GrepLineKey, GrepLineData> Lines => _lines;

    public IReadOnlySet<GrepLineKey> PreRemovedContext => _preRemovedContext;

    public IReadOnlyList<string> MatchingFiles => _matchingFiles;

    public IReadOnlyList<GrepFileCount> CountPage => _countPage;

    public long MatchResultsSeen => _matchResultsSeen;

    public long MatchingFileCount => _matchingFileCount;

    public long MatchingLines => _matchingLines;

    public long Occurrences => _occurrences;

    public bool ExtraResultSeen => _extraResultSeen;

    public bool HasAnomaly => _anomaly;

    public bool Handle(ReadOnlySpan<byte> record)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(record.ToArray());
        }
        catch (JsonException exception)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.RipgrepFailed,
                "Bundled ripgrep emitted malformed JSON.",
                innerException: exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string? type = root.GetProperty("type").GetString();
            JsonElement data = root.GetProperty("data");
            return type switch
            {
                "begin" => HandleBegin(data),
                "match" => HandleLine(data, isMatch: true),
                "context" => HandleLine(data, isMatch: false),
                "end" => HandleEnd(data),
                "summary" => true,
                _ => throw new ToolExecutionException(
                    ToolErrorCodes.RipgrepFailed,
                    "Bundled ripgrep emitted an unknown JSON event type."),
            };
        }
    }

    private bool HandleBegin(JsonElement data)
    {
        if (TryReadPath(data.GetProperty("path"), out string path))
        {
            ResetCurrentFile(path);
        }

        return true;
    }

    private bool HandleLine(JsonElement data, bool isMatch)
    {
        if (!TryReadPath(data.GetProperty("path"), out string path) ||
            !TryReadText(data.GetProperty("lines"), out string rawLine))
        {
            AddTextAnomaly("A ripgrep JSON line or path used an unsupported byte representation and was omitted.");
            return true;
        }

        if (!data.TryGetProperty("line_number", out JsonElement lineNumberElement) ||
            !lineNumberElement.TryGetInt64(out long lineNumber) ||
            lineNumber < 1)
        {
            throw new ToolExecutionException(
                ToolErrorCodes.RipgrepFailed,
                "Bundled ripgrep emitted an invalid line number.");
        }

        if (!StringComparer.Ordinal.Equals(_currentLinePath, path))
        {
            ResetCurrentFile(path);
        }

        string line = StripOneLineTerminator(rawLine);
        GrepExcerpt prefixExcerpt = GrepExcerptFactory.Prefix(line);
        int occurrenceCount = 0;
        GrepExcerpt matchExcerpt = prefixExcerpt;

        if (isMatch)
        {
            JsonElement submatches = data.GetProperty("submatches");
            occurrenceCount = submatches.GetArrayLength();
            if (occurrenceCount <= 0)
            {
                throw new ToolExecutionException(
                    ToolErrorCodes.RipgrepFailed,
                    "Bundled ripgrep emitted a match without submatches.");
            }

            JsonElement first = submatches[0];
            int start = first.GetProperty("start").GetInt32();
            int end = first.GetProperty("end").GetInt32();
            matchExcerpt = GrepExcerptFactory.AroundFirstMatch(line, start, end);
            if (request.OutputMode is not GrepOutputMode.Count)
            {
                _matchingLines++;
                _occurrences += occurrenceCount;
            }

            _matchResultsSeen++;
        }

        bool selected = isMatch && request.OutputMode is GrepOutputMode.Matches &&
            _matchResultsSeen > request.ResultOffset &&
            _matchResultsSeen <= (long)request.ResultOffset + request.MaxResults;
        bool extraMatch = isMatch && request.OutputMode is GrepOutputMode.Matches &&
            _matchResultsSeen > (long)request.ResultOffset + request.MaxResults;

        if (selected)
        {
            GrepSelectedMatch selectedMatch = new(path, lineNumber, matchExcerpt, occurrenceCount);
            _selectedMatches.Add(selectedMatch);
            _selectedLinesInCurrentFile.Add(lineNumber);
            foreach (GrepLineData prior in _priorLines)
            {
                if (prior.LineNumber >= lineNumber - request.ContextLines)
                {
                    AddLine(prior);
                }
            }

            GrepLineData selectedLine = new(path, lineNumber, matchExcerpt, true, occurrenceCount);
            AddLine(selectedLine);
            _lastSelectedPath = path;
            _lastSelectedLine = lineNumber;
        }
        else
        {
            GrepLineData contextLine = new(path, lineNumber, prefixExcerpt, false, 0);
            if (IsWithinSelectedContext(lineNumber))
            {
                AddLine(contextLine);
            }
        }

        EnqueuePrior(new GrepLineData(
            path,
            lineNumber,
            selected ? matchExcerpt : prefixExcerpt,
            selected,
            selected ? occurrenceCount : 0));

        if (request.OutputMode is GrepOutputMode.FilesWithMatches && isMatch)
        {
            if (!StringComparer.Ordinal.Equals(_lastMatchingFilePath, path))
            {
                _lastMatchingFilePath = path;
                _matchingFileCount++;
                if (_matchingFileCount > request.ResultOffset &&
                    _matchingFileCount <= (long)request.ResultOffset + request.MaxResults)
                {
                    _matchingFiles.Add(path);
                }
                else if (_matchingFileCount > (long)request.ResultOffset + request.MaxResults)
                {
                    _extraResultSeen = true;
                    return false;
                }
            }
        }

        if (extraMatch)
        {
            _extraResultSeen = true;
            return !SelectedContextIsComplete(path, lineNumber);
        }

        if (_extraResultSeen && SelectedContextIsComplete(path, lineNumber))
        {
            return false;
        }

        return true;
    }

    private bool HandleEnd(JsonElement data)
    {
        if (data.TryGetProperty("binary_offset", out JsonElement binaryOffset) &&
            binaryOffset.ValueKind is not JsonValueKind.Null)
        {
            AddTextAnomaly("Bundled ripgrep detected binary or unsupported text data; results are incomplete.");
        }

        if (request.OutputMode is GrepOutputMode.Count &&
            TryReadPath(data.GetProperty("path"), out string path))
        {
            JsonElement stats = data.GetProperty("stats");
            long matchingLines = stats.GetProperty("matched_lines").GetInt64();
            long occurrences = stats.GetProperty("matches").GetInt64();
            if (matchingLines > 0)
            {
                _matchingFileCount++;
                _matchingLines += matchingLines;
                _occurrences += occurrences;
                if (_matchingFileCount > request.ResultOffset &&
                    _matchingFileCount <= (long)request.ResultOffset + request.MaxResults)
                {
                    GrepFileCount count = new(path, matchingLines, occurrences);
                    int bytes = JsonSerializer.SerializeToUtf8Bytes(count).Length;
                    if (bytes > ToolBudgets.PathRecordBytes)
                    {
                        AddTextAnomaly("A per-file count record exceeded the fixed output-record budget and was omitted.");
                    }
                    else
                    {
                        _countPage.Add(count);
                    }
                }
            }
        }

        if (_extraResultSeen && _lastSelectedPath is not null &&
            TryReadPath(data.GetProperty("path"), out string endPath) &&
            StringComparer.Ordinal.Equals(endPath, _lastSelectedPath))
        {
            return false;
        }

        return true;
    }

    private bool SelectedContextIsComplete(string path, long lineNumber)
    {
        if (request.ContextLines == 0 || _lastSelectedPath is null)
        {
            return true;
        }

        if (!StringComparer.Ordinal.Equals(path, _lastSelectedPath))
        {
            return true;
        }

        return lineNumber >= _lastSelectedLine + request.ContextLines;
    }

    private bool IsWithinSelectedContext(long lineNumber)
    {
        if (request.ContextLines == 0 || _selectedLinesInCurrentFile.Count == 0)
        {
            return false;
        }

        for (int index = _selectedLinesInCurrentFile.Count - 1; index >= 0; index--)
        {
            long selected = _selectedLinesInCurrentFile[index];
            if (selected < lineNumber - request.ContextLines)
            {
                break;
            }

            if (Math.Abs(selected - lineNumber) <= request.ContextLines)
            {
                return true;
            }
        }

        return false;
    }

    private void EnqueuePrior(GrepLineData line)
    {
        if (request.ContextLines == 0)
        {
            return;
        }

        _priorLines.Enqueue(line);
        while (_priorLines.Count > request.ContextLines)
        {
            _priorLines.Dequeue();
        }
    }

    private void AddLine(GrepLineData line)
    {
        GrepLineKey key = new(line.Path, line.LineNumber);
        if (_lines.TryGetValue(key, out GrepLineData? existing))
        {
            if (!existing.IsSelectedMatch)
            {
                _retainedContextBytes -= existing.Excerpt.ReturnedUtf8Bytes;
                _retainedContextLines--;
            }

            if (existing.IsSelectedMatch && !line.IsSelectedMatch)
            {
                return;
            }
        }

        _lines[key] = line;
        _preRemovedContext.Remove(key);
        if (!line.IsSelectedMatch)
        {
            _retainedContextBytes += line.Excerpt.ReturnedUtf8Bytes;
            _retainedContextLines++;
            PruneRetainedContext();
        }
    }

    private void PruneRetainedContext()
    {
        if (_retainedContextBytes <= RetainedContextBytesMaximum &&
            _retainedContextLines <= RetainedContextLinesMaximum)
        {
            return;
        }

        IReadOnlyList<GrepLineKey> removalOrder = GrepBlockBuilder.ContextRemovalOrder(
            _selectedMatches,
            _lines);
        int targetBytes = (RetainedContextBytesMaximum * 9) / 10;
        int targetLines = (RetainedContextLinesMaximum * 9) / 10;
        foreach (GrepLineKey key in removalOrder)
        {
            if (_retainedContextBytes <= targetBytes && _retainedContextLines <= targetLines)
            {
                break;
            }

            if (_lines.Remove(key, out GrepLineData? removed) && !removed.IsSelectedMatch)
            {
                _retainedContextBytes -= removed.Excerpt.ReturnedUtf8Bytes;
                _retainedContextLines--;
                _preRemovedContext.Add(key);
            }
        }
    }

    private void ResetCurrentFile(string path)
    {
        _currentLinePath = path;
        _priorLines.Clear();
        _selectedLinesInCurrentFile.Clear();
    }

    private bool TryReadPath(JsonElement pathElement, out string path)
    {
        if (!TryReadText(pathElement, out string rawPath))
        {
            path = string.Empty;
            return false;
        }

        try
        {
            path = fileSystem.NormalizeAbsolutePath(
                Path.IsPathFullyQualified(rawPath)
                    ? rawPath
                    : Path.Combine(workingDirectory, rawPath));
            OutputBudget.EnsurePathFits(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ToolExecutionException)
        {
            path = string.Empty;
            AddTextAnomaly("A ripgrep path could not be normalized or represented and was omitted.");
            return false;
        }
    }

    private static bool TryReadText(JsonElement value, out string text)
    {
        if (value.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind is JsonValueKind.String)
        {
            string? candidate = textElement.GetString();
            if (candidate is not null)
            {
                text = candidate;
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    private void AddTextAnomaly(string message)
    {
        _anomaly = true;
        warnings.Add(ToolWarningCodes.TextAnomaly, message, null);
    }

    private static string StripOneLineTerminator(string line)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return line[..^2];
        }

        return line.EndsWith('\n') || line.EndsWith('\r') ? line[..^1] : line;
    }
}
