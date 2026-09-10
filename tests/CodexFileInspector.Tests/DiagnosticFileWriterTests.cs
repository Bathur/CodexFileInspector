using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexFileInspector.Diagnostics;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class DiagnosticFileWriterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Idle_and_disabled_writers_do_not_touch_the_file_system()
    {
        RecordingStore store = new();
        using DiagnosticFileWriter writer = new("unused", store);
        writer.Dispose();
        await writer.Completion.WaitAsync(Timeout);

        Assert.Equal(0, store.DirectoryCalls);
        Assert.Empty(store.Streams);
        Assert.False(writer.IsEnabled);
        Assert.False(DisabledDiagnosticWriter.Instance.IsEnabled);
        DisabledDiagnosticWriter.Instance.TryWrite(Record(1));
        DisabledDiagnosticWriter.Instance.Dispose();
    }

    [Fact]
    public async Task A_full_queue_discards_new_records_without_waiting_for_the_writer()
    {
        RecordingStore store = new(blockWrites: true);
        using DiagnosticFileWriter writer = new("unused", store, capacity: 2);
        writer.TryWrite(Record(0));
        await store.WriteStarted.Task.WaitAsync(Timeout);

        await Task.Run(() =>
        {
            writer.TryWrite(Record(1));
            writer.TryWrite(Record(2));
            writer.TryWrite(Record(3));
            writer.TryWrite(Record(4));
        }).WaitAsync(Timeout);
        store.ReleaseWrites();
        await store.WaitForFlushes(3).WaitAsync(Timeout);
        writer.Dispose();
        await writer.Completion.WaitAsync(Timeout);

        Assert.Equal([0, 1, 2], ReadIds(Assert.Single(store.Streams).ToArray()));
    }

    [Fact]
    public async Task Dispose_does_not_wait_for_a_blocked_write_or_drain_the_queue()
    {
        RecordingStore store = new(blockWrites: true);
        DiagnosticFileWriter writer = new("unused", store);
        writer.TryWrite(Record(0));
        await store.WriteStarted.Task.WaitAsync(Timeout);
        writer.TryWrite(Record(1));

        await Task.Run(writer.Dispose).WaitAsync(Timeout);
        Assert.False(writer.IsEnabled);
        Assert.False(writer.Completion.IsCompleted);
        writer.TryWrite(Record(2));
        store.ReleaseWrites();
        await writer.Completion.WaitAsync(Timeout);

        RecordingStream stream = Assert.Single(store.Streams);
        Assert.True(stream.WasDisposed);
        Assert.Equal([0], ReadIds(stream.ToArray()));
    }

    [Fact]
    public async Task Concurrent_submissions_produce_one_complete_JSON_line_per_record()
    {
        RecordingStore store = new();
        using DiagnosticFileWriter writer = new("unused", store, capacity: 128);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() => writer.TryWrite(Record(index)))));
        await store.WaitForFlushes(100).WaitAsync(Timeout);
        writer.Dispose();
        await writer.Completion.WaitAsync(Timeout);

        Assert.Equal(Enumerable.Range(0, 100), ReadIds(Assert.Single(store.Streams).ToArray()).Order());
    }

    [Fact]
    public async Task Rotation_keeps_each_record_and_newline_together_and_only_cleans_at_open()
    {
        RecordingStore store = new();
        using DiagnosticFileWriter writer = new("unused", store, maximumFileBytes: 16);
        for (int index = 0; index < 5; index++)
        {
            writer.TryWrite(Record(index));
        }

        await store.WaitForFlushes(5).WaitAsync(Timeout);
        writer.Dispose();
        await writer.Completion.WaitAsync(Timeout);

        Assert.Equal([16, 16, 8], store.Streams.Select(stream => stream.ToArray().Length));
        Assert.Equal([0, 1, 2, 3, 4], store.Streams.SelectMany(stream => ReadIds(stream.ToArray())));
        Assert.Equal(3, store.CleanupCalls);
        Assert.All(store.Streams, stream => Assert.True(stream.WasDisposed));
    }

    [Fact]
    public async Task Oversized_records_are_not_queued()
    {
        RecordingStore store = new();
        using DiagnosticFileWriter writer = new("unused", store);
        writer.TryWrite(new byte[DiagnosticFileWriter.MaximumRecordBytes + 1]);
        writer.TryWrite(Record(1));
        await store.WaitForFlushes(1).WaitAsync(Timeout);
        writer.Dispose();
        await writer.Completion.WaitAsync(Timeout);

        Assert.Equal([1], ReadIds(Assert.Single(store.Streams).ToArray()));
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("cleanup")]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("flush")]
    public async Task File_system_failures_disable_logging_without_escaping_or_retrying(string failure)
    {
        RecordingStore store = new(failure: failure);
        using DiagnosticFileWriter writer = new("unused", store);
        writer.TryWrite(Record(0));
        writer.TryWrite(Record(1));
        await writer.Completion.WaitAsync(Timeout);

        Assert.False(writer.IsEnabled);
        int directoryCalls = store.DirectoryCalls;
        writer.TryWrite(Record(2));
        Assert.Equal(directoryCalls, store.DirectoryCalls);
        Assert.Equal(1, store.DirectoryCalls);
        Assert.All(store.Streams, stream => Assert.True(stream.WasDisposed));
    }

    [Fact]
    public async Task Independent_instances_write_distinct_real_files()
    {
        using TestWorkspace workspace = new();
        FileStoreObserver store = new();
        using DiagnosticFileWriter first = new(workspace.Root, store);
        using DiagnosticFileWriter second = new(workspace.Root, store);
        first.TryWrite(Record(1));
        second.TryWrite(Record(2));
        await store.WaitForFlushes(2).WaitAsync(Timeout);
        first.Dispose();
        second.Dispose();
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Timeout);

        string[] files = Directory.GetFiles(workspace.Root);
        Assert.Equal(2, files.Length);
        Assert.Equal([1, 2], files.SelectMany(path => ReadIds(File.ReadAllBytes(path))).Order());
        Assert.All(files, path => Assert.True(DiagnosticFileStore.IsOwnedRegularFile(Path.GetFileName(path), File.GetAttributes(path))));
    }

    [Fact]
    public void Retention_deletes_old_closed_logs_but_preserves_active_and_unrelated_files()
    {
        using TestWorkspace workspace = new();
        DiagnosticFileStore store = new();
        string activePath = workspace.PathFor(OwnedName("20200101"));
        using Stream active = store.CreateFile(activePath);
        active.Write(new byte[8]);
        active.Flush();
        string oldPath = workspace.PathFor(OwnedName("20210101"));
        string newerPath = workspace.PathFor(OwnedName("20220101"));
        File.WriteAllBytes(oldPath, new byte[8]);
        File.WriteAllBytes(newerPath, new byte[8]);
        string unrelatedPath = workspace.PathFor("notes.jsonl");
        File.WriteAllBytes(unrelatedPath, new byte[100]);
        string nearMatchPath = workspace.PathFor("prefix-" + OwnedName("20190101"));
        File.WriteAllBytes(nearMatchPath, new byte[100]);
        string directoryPath = workspace.PathFor(OwnedName("20180101"));
        Directory.CreateDirectory(directoryPath);

        // Windows directory enumeration can report stale lengths for open files,
        // so this test forces cleanup without depending on the active file's size.
        store.Cleanup(workspace.Root, retainedBytes: 0);

        Assert.True(File.Exists(activePath));
        Assert.False(File.Exists(oldPath));
        Assert.False(File.Exists(newerPath));
        Assert.True(File.Exists(unrelatedPath));
        Assert.True(File.Exists(nearMatchPath));
        Assert.True(Directory.Exists(directoryPath));
    }

    [Fact]
    public void Retention_uses_file_write_time_even_when_an_older_process_has_a_newer_log()
    {
        using TestWorkspace workspace = new();
        string oldest = workspace.PathFor(OwnedName("20220101"));
        string middle = workspace.PathFor(OwnedName("20210101"));
        string newest = workspace.PathFor(OwnedName("20200101"));
        foreach (string path in new[] { oldest, middle, newest })
        {
            File.WriteAllBytes(path, new byte[8]);
        }
        File.SetLastWriteTimeUtc(oldest, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(middle, new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        new DiagnosticFileStore().Cleanup(workspace.Root, retainedBytes: 16);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Retention_breaks_equal_file_write_times_by_ordinal_file_name()
    {
        using TestWorkspace workspace = new();
        string first = workspace.PathFor(OwnedName("20200101"));
        string second = workspace.PathFor(OwnedName("20210101"));
        DateTime writeTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (string path in new[] { second, first })
        {
            File.WriteAllBytes(path, new byte[8]);
            File.SetLastWriteTimeUtc(path, writeTime);
        }

        new DiagnosticFileStore().Cleanup(workspace.Root, retainedBytes: 8);

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Archive)]
    [InlineData(FileAttributes.ReparsePoint | FileAttributes.Directory)]
    [InlineData(FileAttributes.Directory)]
    public void Retention_never_selects_link_or_directory_entries(FileAttributes attributes)
    {
        Assert.False(DiagnosticFileStore.IsOwnedRegularFile(OwnedName("20200101"), attributes));
    }

    private static byte[] Record(int index) => Encoding.UTF8.GetBytes($"{{\"i\":{index}}}");

    private static int[] ReadIds(byte[] content) => Encoding.UTF8.GetString(content)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("i").GetInt32();
        }).ToArray();

    private static string OwnedName(string date) => $"diagnostics-{date}T0000000000000Z-p1-{new string('a', 32)}-00000000.jsonl";

    private sealed class RecordingStore(bool blockWrites = false, string? failure = null) : IDiagnosticFileStore
    {
        private readonly Channel<bool> _flushes = Channel.CreateUnbounded<bool>();
        private readonly TaskCompletionSource _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RecordingStream> Streams { get; } = [];
        public int DirectoryCalls { get; private set; }
        public int CleanupCalls { get; private set; }

        public void CreateDirectory(string directory)
        {
            DirectoryCalls++;
            FailAt("directory");
        }

        public void Cleanup(string directory, long retainedBytes)
        {
            CleanupCalls++;
            FailAt("cleanup");
        }

        public Stream CreateFile(string path)
        {
            FailAt("create");
            RecordingStream stream = new(this);
            Streams.Add(stream);
            return stream;
        }

        public void ReleaseWrites() => _releaseWrites.TrySetResult();

        public async Task WaitForFlushes(int count)
        {
            for (int index = 0; index < count; index++)
            {
                await _flushes.Reader.ReadAsync();
            }
        }

        public async Task BeforeWrite()
        {
            WriteStarted.TrySetResult();
            if (blockWrites)
            {
                await _releaseWrites.Task;
            }
            FailAt("write");
        }

        public void AfterFlush()
        {
            FailAt("flush");
            _flushes.Writer.TryWrite(true);
        }

        private void FailAt(string stage)
        {
            if (failure == stage)
            {
                throw new IOException($"Test failure at {stage}.");
            }
        }
    }

    private sealed class RecordingStream(RecordingStore store) : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await store.BeforeWrite();
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            store.AfterFlush();
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return base.DisposeAsync();
        }
    }

    private sealed class FileStoreObserver : IDiagnosticFileStore
    {
        private readonly DiagnosticFileStore _inner = new();
        private readonly Channel<bool> _flushes = Channel.CreateUnbounded<bool>();

        public void CreateDirectory(string directory) => _inner.CreateDirectory(directory);
        public void Cleanup(string directory, long retainedBytes) => _inner.Cleanup(directory, retainedBytes);
        public Stream CreateFile(string path) => new FlushObserverStream(_inner.CreateFile(path), _flushes.Writer);

        public async Task WaitForFlushes(int count)
        {
            for (int index = 0; index < count; index++)
            {
                await _flushes.Reader.ReadAsync();
            }
        }
    }

    private sealed class FlushObserverStream(Stream inner, ChannelWriter<bool> flushed) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            flushed.TryWrite(true);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
