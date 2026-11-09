using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Index-based live accessors + immediate handlers of the custom-theme colour editor, shared by the house-style view
/// (<c>ThemeEditorBody.Colours.cs</c>, the "Edit colours" child window). The slot rows pull
/// <see cref="IThemeOverrides.Slots"/> live (no cap — the lists are virtual), so slots registered AFTER the window
/// was built still appear. Retained uGUI applies at once, so none of the old IMGUI Layout-vs-Repaint deferral is needed.
/// </summary>
internal sealed partial class ThemeEditorBody
{
    // Pooled rows of the two VIRTUAL colour lists (Other colours, Add-override picker). There is no cap on the slot
    // count: the old 64-row eager pools (and a 64 cap in SlotRowCount) silently dropped every colour past the 64th
    // registered one. The pool only has to cover the fixed viewports (166 / 132 px at a 28 px pitch) + margin.
    private const int PoolRows = 9;

    /// <summary>The framework slots the editor shows in its own "Base" and "Status" panels (in that order); every other
    /// slot goes to "Other colours".</summary>
    internal static readonly string[] BaseKeys =
        { "Theme.MenuBackground", "Theme.MenuText", "Theme.MenuMuted", "Theme.MenuAccent", "Theme.MenuBorder" };
    internal static readonly string[] StatusKeys = { "Theme.Success", "Theme.Danger", "Theme.Caution" };

    /// <summary>Per-frame tick from the uGUI hub (Host TickOverlayServices). Coalesces ColorPicker-drag
    /// edits to a single persist+rebake on mouse-release — the uGUI analog of the IMGUI per-Draw flush.</summary>
    public void TickUgui() => FlushEditsOnRelease();

    // ---- index-based live accessors (slot set may grow after build; pull live) ----

    private int SlotRowCount() => _overrides.SlotCount;

    // Cache the slot list — IThemeOverrides.Slots allocates a fresh List + a record per slot on every call,
    // and the editor's row Funcs hit SlotAt() dozens of times per poll. Refresh when the registered count
    // changes (late plugin registration) OR Revision changes (a Relabel — e.g. a language switch — never
    // changes the count, so SlotCount alone would miss it), so the steady-state poll allocates nothing here.
    private IReadOnlyList<ColorSlotInfo>? _slotCache;
    private int _slotCacheCount = -1;
    private int _slotCacheRevision = -1;
    private ColorSlotInfo? SlotAt(int i)
    {
        // Lazy, RENDER-driven initial relabel (see ColorReg's doc, ThemeEditorBody.cs): SlotAt is reachable
        // only when this editor's own Funcs are pulled by WindowService, which never happens while the
        // Settings window is hidden — so this relabel's first client-language read runs only AFTER the
        // player has actually opened Settings, not merely after some tick count since boot.
        if (!_colorLabelsRelabeled) { RelabelFrameworkColors(); _colorLabelsRelabeled = true; }
        if (SlotCacheRefresh.ShouldRefresh(_overrides.SlotCount, _overrides.Revision, _slotCacheCount, _slotCacheRevision))
        {
            _slotCache = _overrides.Slots;
            _slotCacheCount = _overrides.SlotCount;
            _slotCacheRevision = _overrides.Revision;
        }
        var list = _slotCache;
        return list != null && i >= 0 && i < list.Count ? list[i] : null;
    }
    private string KeyAt(int i) => SlotAt(i)?.Key ?? "";
    private string LabelAt(int i) => SlotAt(i) is { } s ? $"{OwnerDisplay(s.Owner)} · {s.Label}" : "";
    private string HexOf(string key) => key.Length > 0 ? ToHex(_overrides.Resolve(key)) : "";
    private ColorRgba ColorOf(string key) => key.Length > 0 ? _overrides.Resolve(key) : new ColorRgba(0f, 0f, 0f, 0f);
    private bool HasOverride(string key) => key.Length > 0 && _overrides.HasOverride(key);

    // "Other colours": the remaining framework slots always (Reset-to-default), plugin slots only when overridden (sparse).
    private bool ShowInOtherList(int i)
        => SlotAt(i) is { } s && !IsBaseOrStatus(s.Key) && (s.Owner == SystemOwner || _overrides.HasOverride(s.Key));
    // Picker: addable plugin slots (not system, not already overridden) matching the live filter.
    private bool ShowInPicker(int i)
        => SlotAt(i) is { } s && s.Owner != SystemOwner && !_overrides.HasOverride(s.Key) && MatchesPickerFilter(s);

    private static bool IsBaseOrStatus(string key)
        => System.Array.IndexOf(BaseKeys, key) >= 0 || System.Array.IndexOf(StatusKeys, key) >= 0;

    private bool MatchesPickerFilter(ColorSlotInfo s)
    {
        var f = _pickerFilter ?? "";
        if (f.Length == 0) return true;
        return s.Label.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
            || s.Owner.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>How many colours the active custom theme overrides (the child header's "N colours changed").</summary>
    private int ChangedCount()
    {
        var n = 0;
        for (var i = 0; i < SlotRowCount(); i++) if (SlotAt(i) is { } s && _overrides.HasOverride(s.Key)) n++;
        return n;
    }

    // ---- uGUI immediate handlers (no Layout deferral — retained uGUI applies at once) ----

    private void ToggleExpandKey(string key)
    {
        if (key.Length == 0) return;
        ToggleExpand(key, _overrides.Resolve(key));
    }

    private void ResetKey(string key)
    {
        if (key.Length == 0 || !_overrides.HasOverride(key)) return;
        _overrides.ClearOverride(key);
        if (_expandedSlotKey == key) _expandedSlotKey = null;
        _overrides.Flush();
        _namedTheme.NotifyColorsChanged();
    }

    private void SetCheckedAt(int i, bool on)
    {
        var k = KeyAt(i);
        if (k.Length > 0) SetChecked(k, on);
    }
}
