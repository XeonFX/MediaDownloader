using MediaDownloader.Services.Localization;
using Microsoft.AspNetCore.Components;

namespace MediaDownloader.Components;

/// <summary>
/// Base for pages that display localized text. Subscribes to
/// <see cref="LocalizationService.LanguageChanged"/> and re-renders on a live language switch, so
/// pages no longer each repeat the same subscribe / <c>StateHasChanged</c> / unsubscribe wiring.
/// The service is exposed as <c>L</c> for markup, matching the previous per-page <c>@inject</c>.
/// <para>
/// Pages with their own initialization or cleanup override <see cref="OnInitializedAsync"/> and
/// <see cref="Dispose(bool)"/> and call the base implementation. (The layout keeps its own wiring:
/// it derives from <see cref="LayoutComponentBase"/> and C# is single-inheritance.)
/// </para>
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase, IDisposable
{
    [Inject] protected LocalizationService L { get; set; } = default!;

    protected override void OnInitialized() => L.LanguageChanged += OnLanguageChanged;

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            L.LanguageChanged -= OnLanguageChanged;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
