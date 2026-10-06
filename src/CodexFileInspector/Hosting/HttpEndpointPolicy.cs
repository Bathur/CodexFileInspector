using Microsoft.AspNetCore.Http;

namespace CodexFileInspector.Hosting;

internal sealed class HttpEndpointPolicy(int port)
{
    public bool Allows(HttpRequest request)
    {
        if (!IsLoopbackName(request.Host.Host) || (request.Host.Port ?? 80) != port)
            return false;
        if (!request.Headers.TryGetValue("Origin", out var origins))
            return true;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out Uri? origin))
            return false;
        return origin.Scheme == Uri.UriSchemeHttp && IsLoopbackName(origin.Host) &&
            origin.Port == port && origin.UserInfo.Length == 0 &&
            origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0;
    }

    private static bool IsLoopbackName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.Ordinal) ||
        host.Equals("[::1]", StringComparison.Ordinal) || host.Equals("::1", StringComparison.Ordinal);
}
