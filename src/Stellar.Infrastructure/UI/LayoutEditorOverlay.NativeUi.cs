using System.Collections.Generic;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Native-UI half of <see cref="LayoutEditorOverlay"/>. Contributes edit-mode chrome items for every
/// resolved entry in the bound <see cref="Stellar.Application.Services.NativeUiService"/> (rendered on the
/// uGUI overlay canvas via SyncChrome). The drag input is handled by the main partial's <c>ProcessInput</c>.
/// </summary>
internal sealed partial class LayoutEditorOverlay
{
    private void AddNativeUiItems(List<Stellar.Infrastructure.Game.EditChromeItem> items)
    {
        if (_nativeUi is null) return;
        foreach (var e in _nativeUi.Entries)
        {
            if (!e.IsResolved) continue;   // incl. hidden (don't skip !Visible) — keep a dimmed re-enable outline
            var color = _editor.SelectedWindowId == e.Descriptor.Id ? OutlineSelected : OutlineUnselected;
            // Live rect (not the resolve-time snapshot) so the outline tracks the element's current size/position.
            items.Add(new Stellar.Infrastructure.Game.EditChromeItem(
                _nativeUi.GetLiveRect(e), color, $"[Game UI] {e.Descriptor.DisplayName}",
                e.Descriptor.Id, e.Visible, e.Descriptor.SafeToHide));
        }
    }
}
