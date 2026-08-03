using Microsoft.Extensions.Options;
using MonoTorrent.Client;

namespace MediaDownloader.Services.Downloads;

/// <summary>
/// Creates the MonoTorrent engine <see cref="DownloadManager"/> runs on.
///
/// This exists so tests can stand up a real DownloadManager — and the endpoints above it — without
/// opening peer, DHT, tracker and port-forwarding sockets, by registering a factory that returns
/// null. It replaces a "EnableTorrentEngine" config flag that did the same job: a switch bound from
/// the app's own configuration section could be flipped in a shipped install, leaving the app
/// running with downloads that silently never start. A DI seam can only be moved by code that
/// already runs in the test host.
/// </summary>
public interface ITorrentEngineFactory
{
    /// <summary>Returns the engine to use, or null to run with no torrent engine at all.</summary>
    ClientEngine? Create();
}

/// <summary>The real engine, configured for this app's networking realities.</summary>
public class TorrentEngineFactory : ITorrentEngineFactory
{
    private readonly string _cacheDirectory;

    public TorrentEngineFactory(IOptions<DownloadEngineOptions> options) =>
        _cacheDirectory = string.IsNullOrWhiteSpace(options.Value.CacheDirectory)
            ? AppPaths.TorrentCacheDirectory
            : Path.GetFullPath(options.Value.CacheDirectory);

    public ClientEngine Create()
    {
        var settings = new EngineSettingsBuilder
        {
            AutoSaveLoadFastResume = true,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            CacheDirectory = _cacheDirectory,
            // On networks that filter P2P traffic most outbound peer connections fail. Raising the
            // half-open limit lets the engine churn through unreachable peers faster to reach the ones
            // that do connect, which is what a magnet needs to fetch its metadata. Ports are left at
            // the OS-assigned default: a fixed port risks a hard bind failure if another client (or a
            // not-yet-released previous instance) holds it, which silently kills peer discovery.
            MaximumHalfOpenConnections = 20
        };
        return new ClientEngine(settings.ToSettings());
    }
}
