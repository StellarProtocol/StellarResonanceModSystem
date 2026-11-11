using System.Collections.Generic;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Native-UI half of <see cref="LayoutEditorOverlay"/>. Contributes edit-mode chrome items for every editable
/// entry in the bound <see cref="NativeUiService"/> (rendered on the uGUI overlay canvas via SyncChrome): a live
/// outline for an element the game is drawing, a dashed + hatched PLACEHOLDER (approved prototype style A,
/// <c>_scratch/prototypes/native-ui-edit-placeholders.html</c>) for one that is absent or hidden by the game —
/// so a boss bar / party panel can be placed before it ever appears. The drag input is handled by the main
/// partial's <c>ProcessInput</c>.
/// </summary>
internal sealed partial class LayoutEditorOverlay
{
    // Below this placeholder width the generic hint uses its short form ("Hidden now") so it fits the box.
    private const float ShortHintMaxWidth = 140f;

    private void AddNativeUiItems(List<Stellar.Infrastructure.Game.EditChromeItem> items)
    {
        if (_nativeUi is null) return;
        foreach (var e in _nativeUi.Entries)
        {
            // incl. hidden (don't skip !Visible) — keep a dimmed re-enable outline / placeholder
            if (!_nativeUi.TryGetEditView(e, out var view)) continue;
            var color = _editor.SelectedWindowId == e.Descriptor.Id ? OutlineSelected : OutlineUnselected;
            var name = T(e.Descriptor.DisplayNameKey);
            var item = new Stellar.Infrastructure.Game.EditChromeItem(
                view.Rect, color, $"[Game UI] {name}", e.Descriptor.Id, e.Visible, e.Descriptor.SafeToHide, Resizable: false);
            if (view.Presence != NativeUiPresence.Showing)
                item = item with { Placeholder = true, Caption = name, Hint = PlaceholderHint(e, view.Rect.Width),
                                   SubHint = e.Visible && e.Descriptor.SubHintKey is { } sub ? T(sub) : null };
            items.Add(item);
        }
    }

    // Hidden by the user wins (the element would show otherwise); then the entry's own hint ("Appears during boss
    // fights"); else the generic one, short form in a narrow box.
    private string PlaceholderHint(NativeUiService.EntryState e, float width)
    {
        if (!e.Visible) return T("gameui.ph.hiddenByYou");
        if (e.Descriptor.HintKey is { } key) return T(key);
        return T(width < ShortHintMaxWidth ? "gameui.ph.hiddenShort" : "gameui.ph.hiddenByGame");
    }

    private string T(string key) => _text?.T(key) ?? key;
}
