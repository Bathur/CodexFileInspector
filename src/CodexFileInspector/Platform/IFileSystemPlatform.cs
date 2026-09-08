using CodexFileInspector.Contracts;

namespace CodexFileInspector.Platform;

internal readonly record struct FileChangeStamp(
    uint VolumeSerialNumber,
    ulong FileId,
    long Length,
    long LastWriteTimeFileTimeUtc);

internal readonly record struct DirectoryChangeStamp(
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc);

internal readonly record struct FileSystemEntrySnapshot(
    string Name,
    string Path,
    DirectoryEntryKind Kind);

internal interface IFileSystemPlatform
{
    string NormalizeAbsolutePath(string path);

    FileAttributes GetAttributes(string path);

    FileChangeStamp GetFileChangeStamp(string path);

    FileChangeStamp GetFileChangeStamp(Stream stream);

    DirectoryChangeStamp GetDirectoryChangeStamp(string path);

    Stream OpenRead(string path);

    IEnumerable<FileSystemEntrySnapshot> EnumerateDirectory(string path, CancellationToken cancellationToken);
}
