using System.Globalization;
using CodexFileInspector.Diagnostics;

namespace CodexFileInspector.Hosting;

internal enum ServerTransport { Stdio, Http }

internal sealed record ServerCommandLine(
    ServerTransport Transport,
    int Port,
    bool DiagnosticsEnabled,
    bool ShowVersion,
    bool ShowHelp)
{
    public const int DefaultHttpPort = 43127;
    public const string Usage = "Usage: CodexFileInspector [--transport stdio|http] [--port 1..65535] [--diagnostics]\n" +
        "       CodexFileInspector --version | --help\n" +
        "STDIO is the default. HTTP listens only on 127.0.0.1 (default port 43127), at /mcp. Stop with Ctrl+C.";

    public static ServerCommandLine Parse(string[] arguments)
    {
        DiagnosticCommandLine diagnosticArguments = DiagnosticCommandLine.Parse(arguments);
        ServerTransport transport = ServerTransport.Stdio;
        int port = DefaultHttpPort;
        bool hasTransport = false;
        bool hasPort = false;
        bool version = false;
        bool help = false;
        string[] remaining = diagnosticArguments.HostArguments;
        for (int index = 0; index < remaining.Length; index++)
        {
            switch (remaining[index])
            {
                case "--transport":
                    if (hasTransport || ++index == remaining.Length)
                        throw new ArgumentException("Specify --transport once, followed by stdio or http.");
                    transport = remaining[index] switch
                    {
                        "stdio" => ServerTransport.Stdio,
                        "http" => ServerTransport.Http,
                        _ => throw new ArgumentException("--transport must be stdio or http."),
                    };
                    hasTransport = true;
                    break;
                case "--port":
                    if (hasPort || ++index == remaining.Length ||
                        !int.TryParse(remaining[index], NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                        port is < 1 or > 65535)
                        throw new ArgumentException("Specify --port once, followed by an integer from 1 through 65535.");
                    hasPort = true;
                    break;
                case "--version": version = true; break;
                case "--help": help = true; break;
                default: throw new ArgumentException($"Unknown argument: {remaining[index]}");
            }
        }
        if (hasPort && transport != ServerTransport.Http)
            throw new ArgumentException("--port is available only with --transport http.");
        if ((version || help) && (hasTransport || hasPort || (version && help)))
            throw new ArgumentException("Use --version or --help without transport options.");
        return new(transport, port, diagnosticArguments.Enabled, version, help);
    }
}
