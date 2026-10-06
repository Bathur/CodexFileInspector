using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodexFileInspector.Hosting;

internal sealed class HttpToolCancellation(IHttpContextAccessor httpContextAccessor, IHostApplicationLifetime lifetime)
{
    public McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, cancellationToken) =>
    {
        // Initialize-era HTTP distinguishes disconnection from cancellation:
        // preserve its session and let notifications/cancelled stop the call.
        // New HTTP requests have no session and closing their stream cancels them.
        CancellationToken requestAborted = context.JsonRpcRequest.Context?.RelatedTransport?.SessionId is null
            ? httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None
            : CancellationToken.None;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            requestAborted,
            lifetime.ApplicationStopping);
        return await next(context, linked.Token).ConfigureAwait(false);
    };
}
