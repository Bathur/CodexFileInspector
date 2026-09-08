using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexFileInspector.Contracts;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = false)]
[JsonSerializable(typeof(ReadFileOutput))]
[JsonSerializable(typeof(GrepOutput))]
[JsonSerializable(typeof(GlobOutput))]
[JsonSerializable(typeof(ListDirectoryOutput))]
[JsonSerializable(typeof(DirectoryEntry))]
[JsonSerializable(typeof(ToolWarning))]
internal sealed partial class ToolJsonContext : JsonSerializerContext;
