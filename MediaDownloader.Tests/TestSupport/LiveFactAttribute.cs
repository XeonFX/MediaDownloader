namespace MediaDownloader.Tests.TestSupport;

/// <summary>
/// Marks a test that talks to a real torrent site over the network. Skipped unless
/// <c>MD_LIVE_TESTS=1</c> is set, so a normal <c>dotnet test</c> stays offline, fast and
/// deterministic; CI runs these on a schedule to notice when a site changes its markup or starts
/// blocking us.
/// </summary>
/// <example><code>MD_LIVE_TESTS=1 dotnet test --filter Category=Live</code></example>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "MD_LIVE_TESTS";

    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
            Skip = $"Live network test — set {EnvironmentVariable}=1 to run.";
    }
}
