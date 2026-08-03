using System.Collections.Concurrent;
using FluentAssertions;
using MediaDownloader.Services.Torrents;

namespace MediaDownloader.Tests.Services.Torrents;

/// <summary>
/// Covers <see cref="MirrorRotator{THost}"/>'s staggered fallback: the preferred host goes first
/// and later hosts only start if it hasn't answered in time, so a slow mirror costs one stagger
/// interval instead of a full HTTP timeout.
/// </summary>
public class MirrorRotatorTests
{
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(80);

    private static MirrorRotator<string> Rotator(params string[] hosts) => new(hosts, Stagger);

    private static bool Retryable(Exception ex) => ex is HttpRequestException or TaskCanceledException;

    [Fact]
    public async Task UsesTheFirstHostAndNeverTouchesTheRest_WhenItAnswersQuickly()
    {
        var started = new ConcurrentBag<string>();
        var rotator = Rotator("a", "b", "c");

        var result = await rotator.FetchAsync((host, _) =>
        {
            started.Add(host);
            return Task.FromResult(host);
        }, Retryable, CancellationToken.None);

        result.Should().Be("a");
        started.Should().BeEquivalentTo("a");
    }

    [Fact]
    public async Task StartsTheNextHost_WhenThePreferredOneStalls()
    {
        var started = new ConcurrentBag<string>();
        var rotator = Rotator("slow", "fast");

        var result = await rotator.FetchAsync(async (host, token) =>
        {
            started.Add(host);
            if (host == "slow")
                await Task.Delay(Timeout.Infinite, token);
            return host;
        }, Retryable, CancellationToken.None);

        // "slow" never returns, so the win has to come from the staggered second attempt.
        result.Should().Be("fast");
        started.Should().BeEquivalentTo("slow", "fast");
    }

    [Fact]
    public async Task CancelsLosingAttempts_SoTheyDoNotRunOn()
    {
        var cancelled = new TaskCompletionSource();
        var rotator = Rotator("slow", "fast");

        await rotator.FetchAsync(async (host, token) =>
        {
            if (host == "slow")
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return host;
        }, Retryable, CancellationToken.None);

        // Completes only if the losing attempt actually observed cancellation.
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FallsThroughToALaterHost_WhenEarlierOnesFail()
    {
        var rotator = Rotator("dead", "alsoDead", "alive");

        var result = await rotator.FetchAsync((host, _) => host == "alive"
            ? Task.FromResult(host)
            : Task.FromException<string>(new HttpRequestException("nope")), Retryable, CancellationToken.None);

        result.Should().Be("alive");
    }

    [Fact]
    public async Task RemembersTheWinningHost_ForTheNextCall()
    {
        var rotator = Rotator("dead", "alive");
        Func<string, CancellationToken, Task<string>> fetch = (host, _) => host == "alive"
            ? Task.FromResult(host)
            : Task.FromException<string>(new HttpRequestException("nope"));

        await rotator.FetchAsync(fetch, Retryable, CancellationToken.None);

        // Second call must lead with "alive"; if it still started at "dead" it would have to fail
        // through it again, which this fetch (now failing for *both*) would turn into a throw.
        var second = await rotator.FetchAsync((host, _) => host == "alive"
            ? Task.FromResult(host)
            : Task.FromException<string>(new InvalidOperationException("should not be tried first")),
            Retryable, CancellationToken.None);

        second.Should().Be("alive");
    }

    [Fact]
    public async Task RethrowsTheLastError_WhenEveryHostFails()
    {
        var rotator = Rotator("a", "b");

        var act = () => rotator.FetchAsync<string>(
            (_, _) => Task.FromException<string>(new HttpRequestException("all down")),
            Retryable, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("all down");
    }

    [Fact]
    public async Task PropagatesANonRetryableErrorImmediately()
    {
        var rotator = Rotator("a", "b");

        var act = () => rotator.FetchAsync<string>(
            (_, _) => Task.FromException<string>(new InvalidOperationException("bad response")),
            Retryable, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("bad response");
    }

    [Fact]
    public async Task PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var rotator = Rotator("a", "b");

        var pending = rotator.FetchAsync<string>(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "unreachable";
        }, Retryable, cts.Token);

        await cts.CancelAsync();

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void RejectsAnEmptyHostList()
    {
        var act = () => new MirrorRotator<string>([]);

        act.Should().Throw<ArgumentException>();
    }
}
