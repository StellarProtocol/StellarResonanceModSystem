using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// WHEN the framework's own hotkey descriptions are relabelled into the active language (WHAT is
/// <see cref="FrameworkHotkeyLabels"/>). Construction reads nothing — it runs during wiring, before the client
/// language is safely readable. <see cref="EnsureRelabeled"/> is called from <c>HotkeysPanel</c>'s action-row
/// label Func (render-driven: only pulled while Settings is shown on the Hotkeys tab) and relabels once; a later
/// <see cref="ILocalization.LanguageChanged"/> relabels again. Never call it from a per-tick path.
/// </summary>
internal sealed class FrameworkHotkeyRelabeler
{
    private readonly HotkeyService? _hotkeys;
    private readonly ILocalization _loc;
    private bool _done;

    public FrameworkHotkeyRelabeler(HotkeyService? hotkeys, ILocalization loc)
    {
        _hotkeys = hotkeys;
        _loc = loc;
        // Constructed once in wiring → subscribed once. Fires only on an explicit switch (Settings → Themes).
        _loc.LanguageChanged += Relabel;
    }

    /// <summary>First render-driven relabel; a no-op after the first call.</summary>
    public void EnsureRelabeled()
    {
        if (_done) return;
        _done = true;
        Relabel();
    }

    private void Relabel()
    {
        if (_hotkeys is not null) FrameworkHotkeyLabels.RelabelAll(_hotkeys, _loc);
    }
}
