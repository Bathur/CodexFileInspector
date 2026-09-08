using System.Reflection;
using ModelContextProtocol.Server;

namespace CodexFileInspector.Tools;

internal static class ToolContractCatalog
{
    public static IReadOnlyList<McpServerTool> Create()
    {
        return typeof(ToolContractPrototypes)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(static method => McpServerTool.Create(method))
            .OrderBy(static tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
