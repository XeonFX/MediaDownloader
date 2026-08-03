namespace MediaDownloader.Services.Api;

/// <summary>
/// Caps how fast an agent can make the app hit third-party torrent sites.
///
/// A single search fans out to every enabled source, so an agent stuck in a retry loop turns into a
/// burst of outbound requests from the user's IP. These sites already answer sustained load with
/// Cloudflare challenges — that is exactly how RARBG stopped working — so the cost of not limiting
/// this lands on the user as sources that quietly stop returning results.
///
/// A token bucket rather than a fixed window: an agent legitimately fires a few calls back to back
/// while working through a task, and should not be punished for that, but it must not be able to
/// sustain that rate. Refused calls throw rather than block, so a tool call fails fast with a
/// message the model can act on instead of hanging.
/// </summary>
public class AgentRateLimiter
{
    /// <summary>Calls available for an immediate burst.</summary>
    public const int BurstCapacity = 10;

    /// <summary>How often one call is added back to the bucket.</summary>
    public static readonly TimeSpan RefillInterval = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private double _tokens = BurstCapacity;
    private DateTimeOffset _lastRefill;

    public AgentRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _lastRefill = _time.GetUtcNow();
    }

    /// <summary>
    /// Consumes one allowance for an outbound-traffic operation.
    /// </summary>
    /// <exception cref="AgentApiException">The burst allowance is spent; the message says how long to wait.</exception>
    public void EnsureAllowed(string operation)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _tokens = Math.Min(BurstCapacity,
                _tokens + (now - _lastRefill) / RefillInterval);
            _lastRefill = now;

            if (_tokens < 1)
            {
                var wait = RefillInterval * (1 - _tokens);
                throw new AgentApiException(
                    $"Too many {operation} requests in a short time — each one queries every enabled " +
                    $"torrent site, and hammering them gets this app blocked. Wait about " +
                    $"{Math.Ceiling(wait.TotalSeconds)}s and try again.",
                    StatusCodes.Status429TooManyRequests);
            }

            _tokens -= 1;
        }
    }
}
