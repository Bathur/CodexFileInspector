using CodexFileInspector.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CodexFileInspector.Tests;

public sealed class HttpEndpointPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    public void Standard_http_port_can_be_omitted_from_host_and_origin(string host)
    {
        DefaultHttpContext context = new();
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = $"http://{host}";
        Assert.True(new HttpEndpointPolicy(80).Allows(context.Request));
        Assert.False(new HttpEndpointPolicy(43127).Allows(context.Request));
    }
}
