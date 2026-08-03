using System.Text.Json;

namespace MediaDownloader.Services.Api;

/// <summary>
/// Turns a malformed agent request body into the API's own <see cref="AgentErrorDto"/> envelope.
///
/// Minimal APIs bind the body before any endpoint filter runs, so a deserialization failure never
/// reaches the handler's <c>AgentApiException</c> path — it escapes as a
/// <see cref="BadHttpRequestException"/> and gets rendered by whatever exception page is installed
/// (a full stack trace in development, an HTML error page in production). Neither is much use to an
/// agent, and the stack trace is noise a caller should never see.
///
/// The inner <see cref="JsonException"/> message is worth forwarding: for a type with required
/// members it names exactly which ones were missing, which is precisely what a caller needs to fix
/// its request. Only that message is passed on — never the exception itself.
/// </summary>
public class AgentApiErrorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AgentApiErrorMiddleware> _logger;

    public AgentApiErrorMiddleware(RequestDelegate next, ILogger<AgentApiErrorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!AgentApiAuth.GuardsPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            _logger.LogWarning("Malformed agent request to {Path}: {Message}",
                context.Request.Path, ex.InnerException?.Message ?? ex.Message);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new AgentErrorDto(Describe(ex)));
        }
    }

    private static string Describe(BadHttpRequestException ex) => ex.InnerException switch
    {
        JsonException json => json.Message,
        _ => "The request body could not be read as JSON."
    };
}
