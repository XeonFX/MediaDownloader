using FluentAssertions;
using MediaDownloader.Data;
using MediaDownloader.Services.Torrents;
using MediaDownloader.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaDownloader.Tests.Services.Torrents;

/// <summary>
/// Covers the per-provider outcomes <see cref="TorrentSearchService.SearchStreamAsync"/> reports.
/// These exist so the three ways a search can come up short — a source failing, a source answering
/// with nothing, and the relevance filter discarding rows — are distinguishable instead of all
/// rendering as a silent "No results". That indistinguishability is what let the Nyaa
/// comments-link bug (titles parsed as comment counts, then filtered away) go unnoticed.
/// </summary>
public class ProviderSearchOutcomeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProviderSearchOutcomeTests() => (_factory, _connection) = TestDb.CreateFactory();

    public void Dispose() => _connection.Dispose();

    private TorrentSearchService CreateService(params ITorrentSearchProvider[] providers) =>
        new(providers, _factory, NullLogger<TorrentSearchService>.Instance);

    private Task<IReadOnlyList<ProviderSearchOutcome>> RunAsync(string query, params ITorrentSearchProvider[] providers) =>
        CreateService(providers).SearchStreamAsync(query, provider: null, onResults: _ => Task.CompletedTask);

    [Fact]
    public async Task ReportsCountsForAProviderThatAnswered()
    {
        var provider = new FakeProvider("Good",
            new TorrentSearchResult { Title = "Ubuntu 24.04 desktop" },
            new TorrentSearchResult { Title = "Ubuntu 24.04 server" });

        var outcome = (await RunAsync("ubuntu 24.04", provider)).Single();

        outcome.Status.Should().Be(ProviderSearchStatus.Ok);
        outcome.Returned.Should().Be(2);
        outcome.Filtered.Should().Be(0);
        outcome.Kept.Should().Be(2);
        outcome.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task ReportsAFailingProviderWithoutFailingTheWholeSearch()
    {
        var broken = new ThrowingProvider("Broken",
            new HttpRequestException("blocked", null, System.Net.HttpStatusCode.Forbidden));
        var working = new FakeProvider("Working", new TorrentSearchResult { Title = "ubuntu" });

        var outcomes = await RunAsync("ubuntu", broken, working);

        var failure = outcomes.Single(o => o.Provider == "Broken");
        failure.Status.Should().Be(ProviderSearchStatus.Failed);
        failure.Error.Should().Be("HTTP 403");
        outcomes.Single(o => o.Provider == "Working").Status.Should().Be(ProviderSearchStatus.Ok);
    }

    [Fact]
    public async Task ReportsATimeoutInPlainLanguage()
    {
        var outcome = (await RunAsync("x", new ThrowingProvider("Slow", new TaskCanceledException()))).Single();

        outcome.Error.Should().Be("timed out");
    }

    [Fact]
    public async Task CountsRowsDroppedByTheRelevanceFilter()
    {
        // Exactly the Nyaa failure: rows come back, but their titles were parsed wrong, so the
        // relevance filter discards them. The count is what makes that visible.
        var provider = new FakeProvider("Nyaa",
            new TorrentSearchResult { Title = "[SubsPlease] Mushoku Tensei S3 - 06 (1080p).mkv" },
            new TorrentSearchResult { Title = "3" },
            new TorrentSearchResult { Title = "10" });

        var outcome = (await RunAsync("subsplease mushoku tensei s3 1080p", provider)).Single();

        outcome.Returned.Should().Be(3);
        outcome.Filtered.Should().Be(2);
        outcome.Kept.Should().Be(1);
    }

    [Fact]
    public async Task FlagsAProviderThatAnsweredWithNothingUsable()
    {
        var provider = new FakeProvider("Nyaa",
            new TorrentSearchResult { Title = "3" },
            new TorrentSearchResult { Title = "10" });

        var outcome = (await RunAsync("mushoku tensei", provider)).Single();

        outcome.Status.Should().Be(ProviderSearchStatus.Ok);
        outcome.IsEmpty.Should().BeTrue();
        outcome.Filtered.Should().Be(2);
    }

    [Fact]
    public async Task StreamsEachOutcomeAsItsProviderFinishes()
    {
        var seen = new List<string>();
        var service = CreateService(
            new FakeProvider("A", new TorrentSearchResult { Title = "x" }),
            new ThrowingProvider("B", new HttpRequestException("nope")));

        await service.SearchStreamAsync("x", provider: null,
            onResults: _ => Task.CompletedTask,
            onProviderDone: o => { lock (seen) seen.Add(o.Provider); return Task.CompletedTask; });

        seen.Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAProviderFailure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = CreateService(new ThrowingProvider("A", new OperationCanceledException()));

        var act = () => service.SearchStreamAsync("x", provider: null,
            onResults: _ => Task.CompletedTask, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeProvider : ITorrentSearchProvider
    {
        private readonly IReadOnlyList<TorrentSearchResult> _results;
        public FakeProvider(string name, params TorrentSearchResult[] results)
        {
            Name = name;
            foreach (var r in results) r.Source = name;
            _results = results;
        }

        public string Name { get; }
        public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(_results);
    }

    private sealed class ThrowingProvider(string name, Exception error) : ITorrentSearchProvider
    {
        public string Name => name;
        public Task<IReadOnlyList<TorrentSearchResult>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<TorrentSearchResult>>(error);
    }
}
