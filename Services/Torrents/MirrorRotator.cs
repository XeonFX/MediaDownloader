namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Fetches from a list of mirror hosts, starting with whichever one last succeeded (so a healthy
/// mirror keeps getting used first instead of always starting from index 0) and remembering the new
/// preference on success. Shared by providers that fall back across multiple hosts (LeetxProvider,
/// PirateBayProvider).
///
/// Attempts are <em>staggered</em> rather than strictly sequential: the preferred host goes first,
/// and each later host only starts if no earlier one has answered within <see cref="Stagger"/>. The
/// first success wins and the rest are cancelled. A purely sequential rotation had to burn the full
/// HTTP timeout on a slow host before even trying the next one — measured: apibay.org takes ~16s on
/// a query it hasn't cached, so every such search paid a 5s timeout before falling back to a mirror
/// that answers in 0.4s. Staggering caps that cost at <see cref="Stagger"/> while still preferring
/// the better source when it is merely a little slower.
/// </summary>
internal sealed class MirrorRotator<THost>
{
    /// <summary>
    /// How long to give a host before also trying the next one. Long enough that a healthy mirror
    /// (all of them answer in well under a second) is never doubled up on, short enough that a
    /// stalled host doesn't dominate the search.
    /// </summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromSeconds(1.5);

    private readonly IReadOnlyList<THost> _hosts;
    private readonly TimeSpan _stagger;
    private volatile int _preferred;

    public MirrorRotator(IReadOnlyList<THost> hosts, TimeSpan? stagger = null)
    {
        if (hosts.Count == 0)
            throw new ArgumentException("At least one host is required", nameof(hosts));
        _hosts = hosts;
        _stagger = stagger ?? Stagger;
    }

    /// <summary>
    /// Runs <paramref name="fetch"/> against the hosts in rotation order, staggered by
    /// <see cref="Stagger"/>, and returns the first success — remembering that host as preferred for
    /// next time. Losing attempts are cancelled through the token handed to <paramref name="fetch"/>.
    /// If every host fails with an exception <paramref name="isRetryable"/> accepts, the last one is
    /// rethrown; anything else propagates immediately.
    /// </summary>
    public async Task<TResult> FetchAsync<TResult>(
        Func<THost, CancellationToken, Task<TResult>> fetch, Func<Exception, bool> isRetryable, CancellationToken ct)
    {
        var start = _preferred;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var attempts = Enumerable.Range(0, _hosts.Count)
            .Select(i => AttemptAsync((start + i) % _hosts.Count, _stagger * i, cts.Token))
            .ToList();

        Exception? lastError = null;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);

            if (finished.IsCompletedSuccessfully)
            {
                var (index, result) = finished.Result;
                _preferred = index;
                // Cancel the stragglers, then let them finish unwinding so their exceptions are
                // observed here rather than surfacing later as UnobservedTaskException.
                await cts.CancelAsync();
                await Task.WhenAll(attempts).ContinueWith(_ => { }, TaskScheduler.Default);
                return result;
            }

            var error = finished.Exception?.GetBaseException() ?? new OperationCanceledException();
            // A host we cancelled ourselves isn't a failure worth reporting; only a genuine
            // cancellation of the caller's token is.
            if (ct.IsCancellationRequested)
                throw error;
            if (!isRetryable(error))
                throw error;
            lastError = error;
        }

        throw lastError!;

        async Task<(int Index, TResult Result)> AttemptAsync(int index, TimeSpan delay, CancellationToken token)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);
            return (index, await fetch(_hosts[index], token));
        }
    }
}
