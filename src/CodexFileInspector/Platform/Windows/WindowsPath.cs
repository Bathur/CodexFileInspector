using CodexFileInspector.Errors;

namespace CodexFileInspector.Platform.Windows;

internal sealed class PathValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

internal static class WindowsPath
{
    private static readonly string[] DevicePrefixes = [@"\\?\", @"\\.\", @"\??\", @"\\??\"];

    public static string NormalizeAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new PathValidationException(ToolErrorCodes.PathNotAbsolute, "Path must be a fully qualified Windows absolute path.");
        }

        if (DevicePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PathValidationException(ToolErrorCodes.InvalidPath, "Caller-supplied device and extended namespace paths are not supported.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new PathValidationException(ToolErrorCodes.PathNotAbsolute, "Path must be a fully qualified Windows absolute path.");
        }

        if (path.IndexOfAny(['\0', '"', '<', '>', '|', '*', '?']) >= 0)
        {
            throw new PathValidationException(ToolErrorCodes.InvalidPath, "Path contains characters that are not valid in a concrete Windows path.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PathValidationException(ToolErrorCodes.InvalidPath, "Path is not a valid fully qualified Windows path.");
        }
    }
}
