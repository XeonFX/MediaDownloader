namespace MediaDownloader.Services.Torrents;

/// <summary>How one provider fared during a search.</summary>
public enum ProviderSearchStatus
{
    /// <summary>The provider answered. It may still have returned nothing — check <see cref="ProviderSearchOutcome.Returned"/>.</summary>
    Ok,

    /// <summary>The provider threw (HTTP error, timeout, unparseable response). <see cref="ProviderSearchOutcome.Error"/> says why.</summary>
    Failed
}

/// <summary>
/// The per-provider result of one search, reported alongside the results themselves.
///
/// Without this, every way a search can come up short — a site 403ing, a scraper's selector
/// breaking, the relevance filter discarding rows — renders in the UI as an identical, silent
/// "No results", and the only trace is a warning in a log file nobody reads. That is exactly how
/// the Nyaa comments-link bug hid: five of six episodes were parsed with a comment count as their
/// title and then dropped by the relevance filter, for weeks, with nothing on screen to suggest it.
/// </summary>
/// <param name="Provider">Provider display name.</param>
/// <param name="Status">Whether the provider answered at all.</param>
/// <param name="Returned">Rows the provider produced, before relevance filtering.</param>
/// <param name="Filtered">Rows dropped because the title didn't match the query.</param>
/// <param name="Error">Failure message when <paramref name="Status"/> is <see cref="ProviderSearchStatus.Failed"/>.</param>
public record ProviderSearchOutcome(
    string Provider,
    ProviderSearchStatus Status,
    int Returned = 0,
    int Filtered = 0,
    string? Error = null)
{
    /// <summary>Rows that survived filtering and reached the caller.</summary>
    public int Kept => Returned - Filtered;

    /// <summary>
    /// True when the provider answered but nothing survived — the case most likely to mean a broken
    /// scraper or an over-eager filter rather than a genuinely empty catalogue.
    /// </summary>
    public bool IsEmpty => Status == ProviderSearchStatus.Ok && Kept == 0;
}
