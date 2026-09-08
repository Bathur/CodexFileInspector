using System.ComponentModel;
using CodexFileInspector.Contracts;
using CodexFileInspector.Platform.Windows;

namespace CodexFileInspector.Errors;

internal sealed class ToolExecutionException(
    string code,
    string message,
    string? field = null,
    int? index = null,
    string? path = null,
    long? limit = null,
    long? actual = null,
    long? total = null,
    int? osCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;

    public string? Field { get; } = field;

    public int? Index { get; } = index;

    public string? Path { get; } = path;

    public long? Limit { get; } = limit;

    public long? Actual { get; } = actual;

    public long? Total { get; } = total;

    public int? OsCode { get; } = osCode;
}

internal static class ToolExceptionMapper
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public static bool IsExpected(Exception exception) => exception is
        ToolExecutionException or
        ToolValidationException or
        PathValidationException or
        FileNotFoundException or
        DirectoryNotFoundException or
        UnauthorizedAccessException or
        IOException or
        Win32Exception;

    public static ToolError Map(Exception exception, string? path)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string code;
        string message;
        string? field = null;
        int? index = null;
        long? limit = null;
        long? actual = null;
        long? total = null;
        int? osCode = null;

        switch (exception)
        {
            case ToolExecutionException execution:
                code = execution.Code;
                message = execution.Message;
                field = execution.Field;
                index = execution.Index;
                path = execution.Path ?? path;
                limit = execution.Limit;
                actual = execution.Actual;
                total = execution.Total;
                osCode = execution.OsCode;
                break;

            case ToolValidationException validation:
                code = validation.Code;
                message = validation.Message;
                field = validation.Field;
                index = validation.Index;
                limit = validation.Limit;
                actual = validation.Actual;
                break;

            case PathValidationException validation:
                code = validation.Code;
                message = validation.Message;
                field = "path";
                path = null;
                break;

            case FileNotFoundException or DirectoryNotFoundException:
                code = ToolErrorCodes.PathMissing;
                message = "The named path does not exist or is no longer available.";
                break;

            case UnauthorizedAccessException:
                code = ToolErrorCodes.AccessDenied;
                message = "The operating system denied access to the named path.";
                break;

            case Win32Exception win32:
                osCode = win32.NativeErrorCode;
                code = IsSharingViolation(osCode.Value) ? ToolErrorCodes.SharingViolation : ToolErrorCodes.IoError;
                message = code is ToolErrorCodes.SharingViolation
                    ? "Another process temporarily prevented access to the named path."
                    : "The operating system reported an input/output failure.";
                break;

            case IOException io:
                osCode = io.HResult & 0xFFFF;
                code = IsSharingViolation(osCode.Value) ? ToolErrorCodes.SharingViolation : ToolErrorCodes.IoError;
                message = code is ToolErrorCodes.SharingViolation
                    ? "Another process temporarily prevented access to the named path."
                    : "The operating system reported an input/output failure.";
                break;

            default:
                code = ToolErrorCodes.InternalError;
                message = "The Server encountered an unexpected internal failure.";
                path = null;
                break;
        }

        if (!ToolErrorRegistry.All.TryGetValue(code, out ToolErrorDefinition? definition))
        {
            code = ToolErrorCodes.InternalError;
            definition = ToolErrorRegistry.All[code];
            message = definition.Meaning;
            path = null;
        }

        message = Utf8Budget.Truncate(message, ToolBudgets.ErrorMessageBytes, out _);
        if (path is not null)
        {
            try
            {
                OutputBudget.EnsurePathFits(path);
            }
            catch (ToolExecutionException)
            {
                path = null;
            }
        }

        return new ToolError(code, message, definition.Retryable, field, index, path, limit, actual, total, osCode);
    }

    private static bool IsSharingViolation(int errorCode) =>
        errorCode is ErrorSharingViolation or ErrorLockViolation;
}
