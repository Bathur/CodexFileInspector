using CodexFileInspector.Contracts;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexFileInspector.Platform.Windows;

internal sealed class WindowsFileSystemPlatform : IFileSystemPlatform
{
    public string NormalizeAbsolutePath(string path) => WindowsPath.NormalizeAbsolute(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public FileChangeStamp GetFileChangeStamp(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);
        return GetFileChangeStamp(handle);
    }

    public FileChangeStamp GetFileChangeStamp(Stream stream) => stream is FileStream fileStream
        ? GetFileChangeStamp(fileStream.SafeFileHandle)
        : throw new ArgumentException("The Windows filesystem platform requires a FileStream.", nameof(stream));

    public DirectoryChangeStamp GetDirectoryChangeStamp(string path)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException("The directory does not exist.");
        }

        return new DirectoryChangeStamp(directory.CreationTimeUtc, directory.LastWriteTimeUtc);
    }

    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public IEnumerable<FileSystemEntrySnapshot> EnumerateDirectory(
        string path,
        CancellationToken cancellationToken)
    {
        EnumerationOptions options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        foreach (string childPath in Directory.EnumerateFileSystemEntries(path, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(childPath);
            yield return new FileSystemEntrySnapshot(
                Path.GetFileName(childPath),
                Path.GetFullPath(childPath),
                Classify(attributes));
        }
    }

    internal static DirectoryEntryKind Classify(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return DirectoryEntryKind.Link;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return DirectoryEntryKind.Directory;
        }

        if ((attributes & FileAttributes.Device) == 0)
        {
            return DirectoryEntryKind.File;
        }

        return DirectoryEntryKind.Other;
    }

    private static FileChangeStamp GetFileChangeStamp(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read file change information.");
        }

        long length = unchecked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow));
        ulong fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new FileChangeStamp(
            information.VolumeSerialNumber,
            fileId,
            length,
            information.LastWriteTime.ToInt64());
    }
}
