using System.Net;

namespace MediaDownloader.Services.Api;

/// <summary>Why a request to the agent API was allowed or turned away.</summary>
public enum AgentAuthResult
{
    Allowed,

    /// <summary>The feature is switched off in Settings; the endpoints behave as if they don't exist.</summary>
    Disabled,

    /// <summary>Request carried a cross-origin <c>Origin</c> header — i.e. it came from a web page.</summary>
    ForbiddenOrigin,

    /// <summary>Came from another machine without a valid bearer token.</summary>
    Unauthorized,

    /// <summary>The feature is enabled locally but remote access is switched off.</summary>
    RemoteDisabled,

    /// <summary>A remote caller attempted to send the bearer token over plaintext HTTP.</summary>
    InsecureTransport
}

/// <summary>
/// Decides whether a request may use the agent API. Pure and static so the whole policy can be
/// tested exhaustively without standing up a server — it is the one piece here where a mistake is
/// a security bug rather than a bug.
///
/// The rules, in order:
/// <list type="number">
/// <item><b>Feature off</b> → 404, indistinguishable from the endpoints not existing.</item>
/// <item><b>Cross-origin <c>Origin</c> header</b> → 403, always, token or not. Browsers attach
/// <c>Origin</c> to cross-site requests; curl and MCP clients don't. Without this rule, any page
/// you visited could POST to <c>http://localhost:47820/api/downloads</c> and queue downloads on
/// your machine, because loopback needs no token. It is also what the MCP specification requires of
/// local HTTP servers, as a DNS-rebinding defence: a rebound <c>evil.com</c> resolving to 127.0.0.1
/// still sends <c>Origin: http://evil.com</c>.</item>
/// <item><b>Loopback</b> → allowed with no token, as configured.</item>
/// <item><b>Remote access off</b> → 404, even if a token is presented.</item>
/// <item><b>Remote plaintext HTTP</b> → 426; bearer tokens never cross the LAN unencrypted.</item>
/// <item><b>Remote HTTPS</b> → a matching bearer token is required.</item>
/// </list>
/// </summary>
public static class AgentApiAuth
{
    /// <summary>Path prefixes this policy guards, including the API's discovery document.</summary>
    public static bool GuardsPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp")
        || path.StartsWithSegments("/openapi");

    /// <param name="access">Current feature state and token.</param>
    /// <param name="remoteIp">Connection's remote address; null is treated as remote, not local.</param>
    /// <param name="origin">The request's <c>Origin</c> header, if any.</param>
    /// <param name="host">The request's <c>Host</c> header, used to recognise a same-origin call.</param>
    /// <param name="authorization">The request's <c>Authorization</c> header, if any.</param>
    public static AgentAuthResult Evaluate(
        AgentAccess.Snapshot access, IPAddress? remoteIp, bool isHttps, string? origin, string? host,
        string? authorization)
    {
        if (!access.Enabled)
            return AgentAuthResult.Disabled;

        if (!string.IsNullOrEmpty(origin) && !IsAllowedLoopbackOrigin(origin, host))
            return AgentAuthResult.ForbiddenOrigin;

        if (IsLoopback(remoteIp))
            return AgentAuthResult.Allowed;

        if (!access.AllowRemote)
            return AgentAuthResult.RemoteDisabled;

        if (!isHttps)
            return AgentAuthResult.InsecureTransport;

        return AgentAccess.TokenMatches(ExtractBearerToken(authorization), access.Token)
            ? AgentAuthResult.Allowed
            : AgentAuthResult.Unauthorized;
    }

    /// <summary>
    /// True when the request came from this machine. An IPv4-mapped IPv6 address (::ffff:127.0.0.1,
    /// which is what a dual-stack socket reports) has to be unwrapped first, or a perfectly local
    /// request would be treated as remote.
    /// </summary>
    public static bool IsLoopback(IPAddress? remoteIp)
    {
        if (remoteIp is null)
            return false; // in-process/unknown callers don't get the local exemption

        var address = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;
        return IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// True only for the app's own literal loopback origin. Merely matching the Host header is not
    /// enough: after a DNS rebind, <c>Origin: http://evil.example</c> and <c>Host: evil.example</c>
    /// also match while the connection lands on 127.0.0.1. Restricting this exception to localhost
    /// and loopback IP literals keeps that request forbidden, including while LAN binding is on.
    /// </summary>
    private static bool IsAllowedLoopbackOrigin(string origin, string? host)
    {
        if (string.IsNullOrEmpty(host) || !Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;

        var originHost = originUri.Host.Trim('[', ']');
        var isLoopbackHost = originHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(originHost, out var originIp) && IPAddress.IsLoopback(originIp);
        if (!isLoopbackHost)
            return false;

        // Host header carries "host:port" (port omitted when it's the scheme default).
        var originAuthority = originUri.IsDefaultPort
            ? originUri.Host
            : $"{originUri.Host}:{originUri.Port}";

        return string.Equals(originAuthority, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pulls the token out of an <c>Authorization: Bearer &lt;token&gt;</c> header.</summary>
    public static string? ExtractBearerToken(string? authorization)
    {
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var token = authorization[scheme.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}

/// <summary>
/// Keeps the Blazor UI loopback-only and applies <see cref="AgentApiAuth"/> to every agent route.
/// Written as middleware
/// rather than an endpoint filter so the same policy covers the MCP endpoints, which the SDK maps
/// itself, without depending on how it builds them.
/// </summary>
public class AgentApiAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AgentAccess _access;
    private readonly ILogger<AgentApiAuthMiddleware> _logger;

    public AgentApiAuthMiddleware(RequestDelegate next, AgentAccess access, ILogger<AgentApiAuthMiddleware> logger)
    {
        _next = next;
        _access = access;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!AgentApiAuth.GuardsPath(context.Request.Path))
        {
            // A wildcard/HTTPS bind may be configured for the agent API, but that must not expose
            // the Blazor UI, Settings page, static assets, health check, or SignalR circuit to LAN
            // clients. Behave as though every non-agent route is absent off-machine.
            if (AgentApiAuth.IsLoopback(context.Connection.RemoteIpAddress))
                await _next(context);
            else
                await WriteNotFoundAsync(context);
            return;
        }

        var snapshot = await _access.GetAsync(context.RequestAborted);
        var result = AgentApiAuth.Evaluate(
            snapshot,
            context.Connection.RemoteIpAddress,
            context.Request.IsHttps,
            context.Request.Headers.Origin.FirstOrDefault(),
            context.Request.Host.Value,
            context.Request.Headers.Authorization.FirstOrDefault());

        if (result == AgentAuthResult.Allowed)
        {
            await _next(context);
            return;
        }

        if (result != AgentAuthResult.Disabled)
            _logger.LogWarning("Agent API request to {Path} refused ({Result}) from {RemoteIp}",
                context.Request.Path, result, context.Connection.RemoteIpAddress);

        await WriteRefusalAsync(context, result);
    }

    private static Task WriteRefusalAsync(HttpContext context, AgentAuthResult result)
    {
        var (status, message) = result switch
        {
            AgentAuthResult.Disabled => (StatusCodes.Status404NotFound,
                "The agent API is turned off. Enable it in MediaDownloader under Settings → Agent access."),
            AgentAuthResult.RemoteDisabled => (StatusCodes.Status404NotFound,
                "Remote agent access is turned off."),
            AgentAuthResult.InsecureTransport => (StatusCodes.Status426UpgradeRequired,
                "Remote agent requests require HTTPS. Use a local TLS reverse proxy or configure Kestrel with an HTTPS certificate."),
            AgentAuthResult.ForbiddenOrigin => (StatusCodes.Status403Forbidden,
                "Cross-origin requests are not accepted by the agent API."),
            _ => (StatusCodes.Status401Unauthorized,
                "Requests from other machines need a bearer token. Find it in Settings → Agent access.")
        };

        context.Response.StatusCode = status;
        if (result == AgentAuthResult.InsecureTransport)
            context.Response.Headers.Upgrade = "TLS/1.2, HTTP/1.1";
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static Task WriteNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = "Not found." });
    }
}
