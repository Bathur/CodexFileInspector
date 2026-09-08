using CodexFileInspector.Contracts;
using CodexFileInspector.Platform;

namespace CodexFileInspector.Tests;

internal sealed class TestFileSystemPlatform : IFileSystemPlatform
{
    public FileAttributes Attributes { get; set; } = FileAttributes.Normal;

    public DirectoryChangeStamp DirectoryStampBefore { get; set; } = new(DateTime.UnixEpoch, DateTime.UnixEpoch);

    public DirectoryChangeStamp DirectoryStampAfter { get; set; } = new(DateTime.UnixEpoch, DateTime.UnixEpoch);

    public IEnumerable<FileSystemEntrySnapshot> Entries { get; set; } = [];

    public Stream ReadStream { get; set; } = new MemoryStream();

    public FileChangeStamp InitialFileStamp { get; set; } = new(1, 1, 0, 1);

    public FileChangeStamp FinalHandleStamp { get; set; } = new(1, 1, 0, 1);

    public FileChangeStamp FinalPathStamp { get; set; } = new(1, 1, 0, 1);

    private int _directoryStampCalls;
    private int _streamStampCalls;

    public string NormalizeAbsolutePath(string path) => path;

    public FileAttributes GetAttributes(string path) => Attributes;

    public FileChangeStamp GetFileChangeStamp(string path) => FinalPathStamp;

    public FileChangeStamp GetFileChangeStamp(Stream stream) =>
        _streamStampCalls++ == 0 ? InitialFileStamp : FinalHandleStamp;

    public DirectoryChangeStamp GetDirectoryChangeStamp(string path) =>
        _directoryStampCalls++ == 0 ? DirectoryStampBefore : DirectoryStampAfter;

    public Stream OpenRead(string path) => ReadStream;

    public IEnumerable<FileSystemEntrySnapshot> EnumerateDirectory(
        string path,
        CancellationToken cancellationToken) => Entries;
}
