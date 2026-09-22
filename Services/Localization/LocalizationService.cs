using System.Text.Json;
using MediaDownloader.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaDownloader.Services.Localization;

/// <summary>
/// JSON-file based UI localization. Each file in Resources/i18n/&lt;code&gt;.json defines one
/// language: <c>{ "name": "Polski", "strings": { "nav.search": "Szukaj", ... } }</c>.
/// Dropping a new file in that folder adds the language to the Settings picker — no code changes.
/// Missing keys fall back to English, then to the key itself.
/// </summary>
public class LocalizationService
{
    public const string DefaultLanguage = "en";

    private sealed record LanguagePack(string Name, Dictionary<string, string> Strings);

    private readonly Dictionary<string, LanguagePack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<LocalizationService> _logger;

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary>Raised after the language changes so live components can re-render.</summary>
    public event Action? LanguageChanged;

    public LocalizationService(IDbContextFactory<AppDbContext> dbFactory, ILogger<LocalizationService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        LoadPacks();
    }

    /// <summary>Available languages as (code, native name), English first, the rest alphabetical.</summary>
    public IReadOnlyList<(string Code, string Name)> Languages => _packs
        .Select(p => (Code: p.Key, p.Value.Name))
        .OrderBy(l => l.Code.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string this[string key] => Get(key);

    /// <summary>Looks up a key and applies <see cref="string.Format(string, object[])"/> placeholders.</summary>
    public string Format(string key, params object?[] args) => string.Format(Get(key), args);

    private string Get(string key)
    {
        if (_packs.TryGetValue(CurrentLanguage, out var pack) && pack.Strings.TryGetValue(key, out var value))
            return value;
        if (_packs.TryGetValue(DefaultLanguage, out var fallback) && fallback.Strings.TryGetValue(key, out var enValue))
            return enValue;
        return key;
    }

    /// <summary>Restores the persisted language choice; called once at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.GetSettingsAsync(ct);
            if (_packs.ContainsKey(settings.Language))
                CurrentLanguage = settings.Language;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore language preference; using {Default}", DefaultLanguage);
        }
    }

    /// <summary>
    /// Switches the UI language. Persistence is the caller's job (the Settings page saves
    /// AppSettings.Language through its own change-tracked instance).
    /// </summary>
    public void SetLanguage(string code)
    {
        if (!_packs.ContainsKey(code) || code.Equals(CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            return;
        CurrentLanguage = code;
        LanguageChanged?.Invoke();
    }

    private void LoadPacks()
    {
        var dir = Path.Combine(AppPaths.ContentDirectory, "Resources", "i18n");
        if (!Directory.Exists(dir))
        {
            _logger.LogWarning("Localization directory {Dir} not found; UI falls back to string keys", dir);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var name = doc.RootElement.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(file);
                var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.GetProperty("strings").EnumerateObject())
                    strings[prop.Name] = prop.Value.GetString() ?? prop.Name;
                _packs[Path.GetFileNameWithoutExtension(file)] = new LanguagePack(name, strings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping malformed language file {File}", file);
            }
        }
    }
}
