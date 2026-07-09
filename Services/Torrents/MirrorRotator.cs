namespace MediaDownloader.Services.Torrents;

/// <summary>
/// Tries a list of mirror hosts in rotation, starting from whichever one last succeeded (so a
/// healthy mirror keeps getting used first instead of always starting from index 0), and
/// remembering the new preference on success. Shared by providers that fall back across multiple
/// mirror hosts (LeetxProvider, RarbgProvider, PirateBayProvider).
/// </summary>
internal sealed class MirrorRotator<THost>
{
    private readonly IReadOnlyList<THost> _hosts;
    private volatile int _preferred;

    public MirrorRotator(IReadOnlyList<THost> hosts) => _hosts = hosts;

    /// <summary>
    /// Calls <paramref name="fetch"/> for each host in rotation order until one succeeds, remembers
    /// that host as preferred for next time, and returns its result. If every host fails with an
    /// exception <paramref name="isRetryable"/> accepts, rethrows the last one.
    /// </summary>
    public async Task<TResult> FetchAsync<TResult>(
        Func<THost, Task<TResult>> fetch, Func<Exception, bool> isRetryable, CancellationToken ct)
    {
        var start = _preferred;
        Exception? lastError = null;

        for (var i = 0; i < _hosts.Count; i++)
        {
            var index = (start + i) % _hosts.Count;
            var host = _hosts[index];
            try
            {
                var result = await fetch(host);
                _preferred = index;
                return result;
            }
            catch (Exception ex) when (isRetryable(ex) && !ct.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw lastError!;
    }
}
