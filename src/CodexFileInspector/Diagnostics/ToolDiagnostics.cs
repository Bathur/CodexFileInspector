using System.Text.Json.Serialization.Metadata;
using CodexFileInspector.Contracts;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace CodexFileInspector.Diagnostics;

internal static class ToolDiagnostics
{
    public static CallToolResult CreateResult<T>(
        T output,
        JsonTypeInfo<T> typeInfo,
        IServiceProvider services,
        string tool,
        object arguments,
        Exception? exception = null)
        where T : ToolOutput
    {
        // Only observe a final, successfully constructed standard result. Budget
        // probes and intermediate output objects must never produce log records.
        CallToolResult result = ToolResultFactory.Create(output, typeInfo);
        if (output.Status is not (ToolStatus.Error or ToolStatus.Partial))
        {
            return result;
        }

        try
        {
            IDiagnosticWriter? writer = services.GetService<IDiagnosticWriter>();
            if (writer is { IsEnabled: true })
            {
                byte[] record = DiagnosticRecordEncoder.Encode(
                    tool, arguments, result.StructuredContent!.Value, exception);
                writer.TryWrite(record);
            }
        }
        catch (Exception)
        {
            // Diagnostics are best effort, including event construction and DI.
            // Never feed their failure back into the tool's error mapping.
        }

        return result;
    }
}
