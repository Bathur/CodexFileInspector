using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CodexFileInspector.Diagnostics;

internal interface IDiagnosticWriter : IDisposable
{
    bool IsEnabled { get; }

    // The caller transfers an immutable, bounded UTF-8 JSON record without a newline.
    void TryWrite(byte[] record);
}

internal sealed class DisabledDiagnosticWriter : IDiagnosticWriter
{
    public static DisabledDiagnosticWriter Instance { get; } = new();

    private DisabledDiagnosticWriter() { }

    public bool IsEnabled => false;
    public void TryWrite(byte[] record) { }
    public void Dispose() { }
}

internal sealed class DiagnosticFileWriter : IDiagnosticWriter
{
    internal const int QueueCapacity = 32;
    internal const int MaximumRecordBytes = DiagnosticRecordEncoder.MaximumRecordBytes;
    internal const long MaximumFileBytes = 8 * 1024 * 1024;
    internal const long RetainedBytes = 128 * 1024 * 1024;

    private static readonly byte[] Newline = [(byte)'\n'];
    private readonly Channel<byte[]> _queue;
    private readonly string _directory;
    private readonly IDiagnosticFileStore _store;
    private readonly long _maximumFileBytes;
    private readonly long _retainedBytes;
    private readonly string _filePrefix;
    private int _stopped;

    public DiagnosticFileWriter(string directory)
        : this(directory, new DiagnosticFileStore()) { }

    internal DiagnosticFileWriter(
        string directory,
        IDiagnosticFileStore store,
        int capacity = QueueCapacity,
        long maximumFileBytes = MaximumFileBytes,
        long retainedBytes = RetainedBytes)
    {
        _directory = directory;
        _store = store;
        _maximumFileBytes = maximumFileBytes;
        _retainedBytes = retainedBytes;
        _filePrefix = $"diagnostics-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)}-p{Environment.ProcessId}-{Guid.NewGuid():N}";
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

        // Always schedule the consumer: even a synchronously completing stream must
        // never move disk work into construction, TryWrite, or Dispose.
        Completion = Task.Run(ConsumeAsync);
    }

    public bool IsEnabled => Volatile.Read(ref _stopped) == 0;

    internal Task Completion { get; }

    public void TryWrite(byte[] record)
    {
        if (IsEnabled && record.Length <= MaximumRecordBytes && record.Length + 1L <= _maximumFileBytes)
        {
            // FullMode.Wait makes TryWrite reject a full queue; it does not wait.
            _queue.Writer.TryWrite(record);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _queue.Writer.TryComplete();
        // Do not wait for the consumer, flush, or synchronously dispose its stream.
    }

    private async Task ConsumeAsync()
    {
        Stream? stream = null;
        long fileBytes = 0;
        int sequence = 0;
        try
        {
            while (IsEnabled && await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (IsEnabled && _queue.Reader.TryRead(out byte[]? record))
                {
                    if (stream is null || fileBytes + record.Length + Newline.Length > _maximumFileBytes)
                    {
                        if (stream is not null)
                        {
                            Stream closingStream = stream;
                            stream = null;
                            await closingStream.DisposeAsync().ConfigureAwait(false);
                        }

                        if (!IsEnabled)
                        {
                            break;
                        }

                        _store.CreateDirectory(_directory);
                        _store.Cleanup(_directory, _retainedBytes);
                        if (!IsEnabled)
                        {
                            break;
                        }

                        string name = $"{_filePrefix}-{(sequence++).ToString("D8", CultureInfo.InvariantCulture)}.jsonl";
                        stream = _store.CreateFile(Path.Combine(_directory, name));
                        fileBytes = 0;
                    }

                    if (!IsEnabled)
                    {
                        break;
                    }

                    await stream.WriteAsync(record).ConfigureAwait(false);
                    await stream.WriteAsync(Newline).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    fileBytes += record.Length + Newline.Length;
                }
            }
        }
        catch (Exception)
        {
            // Diagnostics are best effort. One failure disables this instance;
            // never retry, report to STDOUT, or replace a tool's existing result.
        }
        finally
        {
            Dispose();
            while (_queue.Reader.TryRead(out _)) { }
            if (stream is not null)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception) { }
            }
        }
    }
}

internal interface IDiagnosticFileStore
{
    void CreateDirectory(string directory);
    void Cleanup(string directory, long retainedBytes);
    Stream CreateFile(string path);
}

internal sealed class DiagnosticFileStore : IDiagnosticFileStore
{
    private static readonly Regex OwnedFileName = new(
        @"\Adiagnostics-[0-9]{8}T[0-9]{13}Z-p[0-9]+-[0-9a-f]{32}-[0-9]{8,10}\.jsonl\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public void CreateDirectory(string directory)
    {
        DirectoryInfo info = Directory.CreateDirectory(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The diagnostic directory must not be a link.");
        }
    }

    public Stream CreateFile(string path) => new FileStream(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.Read,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    });

    internal static bool IsOwnedRegularFile(string name, FileAttributes attributes) =>
        OwnedFileName.IsMatch(name) &&
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;

    public void Cleanup(string directory, long retainedBytes)
    {
        List<(FileInfo File, long Length, DateTime LastWriteTimeUtc)> candidates = [];
        long total = 0;
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo file || !OwnedFileName.IsMatch(entry.Name))
            {
                continue;
            }

            try
            {
                if (!IsOwnedRegularFile(file.Name, file.Attributes))
                {
                    continue;
                }

                long length = file.Length;
                candidates.Add((file, length, file.LastWriteTimeUtc));
                total += length;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        // An old process may have just rotated a new file: use the file's last
        // write time, not the process start timestamp embedded in its name.
        // Active files disallow FILE_SHARE_DELETE, so their deletion fails safely.
        foreach ((FileInfo file, long length, DateTime _) in candidates
            .OrderBy(candidate => candidate.LastWriteTimeUtc)
            .ThenBy(candidate => candidate.File.Name, StringComparer.Ordinal))
        {
            if (total <= retainedBytes)
            {
                break;
            }

            try
            {
                file.Delete();
                total -= length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
