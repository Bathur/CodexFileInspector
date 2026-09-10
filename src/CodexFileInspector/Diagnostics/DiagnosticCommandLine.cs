namespace CodexFileInspector.Diagnostics;

internal sealed record DiagnosticCommandLine(bool Enabled, string[] HostArguments)
{
    public static DiagnosticCommandLine Parse(string[] arguments) => new(
        arguments.Contains("--diagnostics", StringComparer.Ordinal),
        arguments.Where(static argument => argument != "--diagnostics").ToArray());
}
