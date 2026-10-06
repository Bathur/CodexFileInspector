using CodexFileInspector.Hosting;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class ServerCommandLineTests
{
    [Fact]
    public void Existing_diagnostic_startup_remains_stdio()
    {
        ServerCommandLine options = ServerCommandLine.Parse(["--diagnostics"]);
        Assert.Equal(ServerTransport.Stdio, options.Transport);
        Assert.True(options.DiagnosticsEnabled);
    }

    [Fact]
    public void Http_startup_selects_port_and_diagnostics_in_any_order()
    {
        ServerCommandLine options = ServerCommandLine.Parse(["--port", "50123", "--diagnostics", "--transport", "http"]);
        Assert.Equal(ServerTransport.Http, options.Transport);
        Assert.Equal(50123, options.Port);
        Assert.True(options.DiagnosticsEnabled);
    }

    [Theory]
    [InlineData("--port", "0", "--transport", "http")]
    [InlineData("--port", "65536", "--transport", "http")]
    [InlineData("--port", "-1", "--transport", "http")]
    [InlineData("--port", "abc", "--transport", "http")]
    [InlineData("--port", "43127")]
    [InlineData("--transport", "https")]
    [InlineData("--transport")]
    [InlineData("--transport", "http", "--port")]
    [InlineData("--transport", "http", "--transport", "stdio")]
    [InlineData("--transport", "http", "--port", "43127", "--port", "43128")]
    [InlineData("--transport", "http", "--urls", "http://0.0.0.0:43127")]
    public void Invalid_startup_fails_before_listening(params string[] arguments) =>
        Assert.Throws<ArgumentException>(() => ServerCommandLine.Parse(arguments));
}
