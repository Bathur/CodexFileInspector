using CodexFileInspector.Errors;

namespace CodexFileInspector.Contracts;

internal static class ToolWarningCodes
{
    public const string DirectoryChanged = "directory_changed";
    public const string RipgrepTraversal = "ripgrep_traversal_incomplete";
    public const string TextAnomaly = "text_anomaly";
}

internal sealed class WarningCollector
{
    private readonly List<ToolWarning> _items = [];
    private int _retainedMessageBytes;

    public IReadOnlyList<ToolWarning> Items => _items;

    public int Omitted { get; private set; }

    public void Add(string code, string message, string? path)
    {
        if (_items.Count >= ToolBudgets.WarningCount)
        {
            Omitted++;
            return;
        }

        int remainingMessageBytes = ToolBudgets.WarningMessagesTotalBytes - _retainedMessageBytes;
        if (remainingMessageBytes <= 0)
        {
            Omitted++;
            return;
        }

        int messageLimit = Math.Min(ToolBudgets.WarningMessageBytes, remainingMessageBytes);
        string boundedMessage = Utf8Budget.Truncate(message, messageLimit, out _);
        if (boundedMessage.Length == 0 && message.Length > 0)
        {
            Omitted++;
            return;
        }

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

        _items.Add(new ToolWarning(code, boundedMessage, path));
        _retainedMessageBytes += System.Text.Encoding.UTF8.GetByteCount(boundedMessage);
    }
}
