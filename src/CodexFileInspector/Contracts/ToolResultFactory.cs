using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;

namespace CodexFileInspector.Contracts;

internal static class ToolResultFactory
{
    public static int GetCanonicalByteCount<T>(T output, JsonTypeInfo<T> typeInfo)
        where T : ToolOutput
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.SerializeToUtf8Bytes(output, typeInfo).Length;
    }

    public static CallToolResult Create<T>(T output, JsonTypeInfo<T> typeInfo)
        where T : ToolOutput
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(typeInfo);

        string compactJson = JsonSerializer.Serialize(output, typeInfo);
        int byteCount = Encoding.UTF8.GetByteCount(compactJson);
        if (byteCount > ToolBudgets.CanonicalResultBytes)
        {
            throw new InvalidOperationException(
                $"Canonical tool output is {byteCount} UTF-8 bytes; the limit is {ToolBudgets.CanonicalResultBytes}.");
        }

        JsonElement structuredContent = JsonSerializer.SerializeToElement(output, typeInfo);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = compactJson }],
            StructuredContent = structuredContent,
            IsError = output.Status is ToolStatus.Error,
        };
    }
}
