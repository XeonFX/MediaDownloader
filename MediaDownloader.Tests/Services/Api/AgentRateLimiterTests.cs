using FluentAssertions;
using MediaDownloader.Services.Api;
using Microsoft.Extensions.Time.Testing;

namespace MediaDownloader.Tests.Services.Api;

/// <summary>
/// Covers the cap on how fast an agent can make the app query third-party torrent sites. One search
/// fans out to every enabled source, so an agent in a retry loop becomes a burst of outbound
/// requests from the user's address — the kind of traffic that gets a client Cloudflare-challenged.
/// </summary>
public class AgentRateLimiterTests
{
    private static (AgentRateLimiter Limiter, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        return (new AgentRateLimiter(time), time);
    }

    [Fact]
    public void AllowsAFullBurstUpFront()
    {
        var (limiter, _) = Create();

        // An agent working through a task legitimately fires several calls back to back.
        for (var i = 0; i < AgentRateLimiter.BurstCapacity; i++)
            limiter.EnsureAllowed("search");

        var act = () => limiter.EnsureAllowed("search");
        act.Should().Throw<AgentApiException>();
    }

    [Fact]
    public void RefusesWithA429AndAWaitHint_OnceTheBurstIsSpent()
    {
        var (limiter, _) = Create();
        for (var i = 0; i < AgentRateLimiter.BurstCapacity; i++)
            limiter.EnsureAllowed("search");

        var act = () => limiter.EnsureAllowed("search");

        var error = act.Should().Throw<AgentApiException>().Which;
        error.StatusCode.Should().Be(429);
        // The message has to tell the model what to do, not just that it failed.
        error.Message.Should().Contain("Wait about").And.Contain("s and try again");
    }

    [Fact]
    public void RefillsOneCallPerInterval()
    {
        var (limiter, time) = Create();
        for (var i = 0; i < AgentRateLimiter.BurstCapacity; i++)
            limiter.EnsureAllowed("search");

        time.Advance(AgentRateLimiter.RefillInterval);
        limiter.EnsureAllowed("search"); // the one that refilled

        var act = () => limiter.EnsureAllowed("search");
        act.Should().Throw<AgentApiException>();
    }

    [Fact]
    public void DoesNotAccumulateBeyondTheBurstCapacity()
    {
        var (limiter, time) = Create();

        // Idle for a long while: the bucket must cap, not bank an unbounded burst.
        time.Advance(AgentRateLimiter.RefillInterval * 1000);

        for (var i = 0; i < AgentRateLimiter.BurstCapacity; i++)
            limiter.EnsureAllowed("search");

        var act = () => limiter.EnsureAllowed("search");
        act.Should().Throw<AgentApiException>();
    }

    [Fact]
    public void NamesTheOperationInTheMessage()
    {
        var (limiter, _) = Create();
        for (var i = 0; i < AgentRateLimiter.BurstCapacity; i++)
            limiter.EnsureAllowed("series check");

        var act = () => limiter.EnsureAllowed("series check");

        act.Should().Throw<AgentApiException>().WithMessage("*series check*");
    }
}
