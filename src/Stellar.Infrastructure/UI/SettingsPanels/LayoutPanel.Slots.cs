using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The slot rows of Settings → Layout: [name, "Active" chip, Rename, ✕] (click the row to switch) or, while renaming,
// [field, Save, ✕]. Each slot is one fixed list slot bound by INDEX (≤ 8 slots, no recycling), so hover flags are kept
// per slot and nothing needs a rebind reset.
internal sealed partial class LayoutPanel
{
    private const float RenamePillLabel = 58f;   // "Rename" / "Save" in a 78 px row pill (fits "Ganti nama", "เปลี่ยนชื่อ")
    private const float ActiveChipWidth = 56f;

    private HudElement SlotRow(int idx)
        => new ConditionalElement(() => _renaming == idx, RenameRow(idx), NormalRow(idx));

    private HudElement NormalRow(int idx)
    {
        var s = _k.S;
        bool Active() => idx == _storage.ActiveSlot;
        var hover = _rowHover[idx];
        var name = new ConditionalElement(Active,
            new TextElement(() => NameAt(idx), Color: () => s.Readable(s.Accent(), s.AccentWash()), NoWrap: true) { Bold = true, Ellipsize = true },
            new TextElement(() => NameAt(idx), Color: () => s.Text(), NoWrap: true) { Ellipsize = true });
        var row = StellarStyle.TileRow(
            new CellElement(name, Weight: 1f),
            new ConditionalElement(Active, s.Chip(_k.T("layout.active"), () => true, onClick: null, ActiveChipWidth, s.Accent)),
            s.SmallPill(_k.T("layout.renameSlot"), PillKind.Neutral, () => BeginRename(idx), RenamePillLabel),
            new ConditionalElement(() => _storage.SlotCount > 2,
                s.SmallCloseButton(() => { _confirmRemove = idx; _renaming = -1; }, hover: _xHover[idx]),
                SettingsKit.HelpGap()));
        return new BubbleElement(new SelectableElement(row, OnClick: () => SwitchSlot(idx))
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => s.Fill(Active() ? s.AccentWash() : hover.On ? s.Hover(s.Tile()) : s.Tile()),
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private HudElement RenameRow(int idx)
    {
        var s = _k.S;
        var row = StellarStyle.TileRow(
            new CellElement(new InputElement(() => _renameBuffer, v => { _renameBuffer = v ?? ""; CommitRename(idx); }, 120f,
                OnChange: v => _renameBuffer = v ?? "") { SingleLine = true, FocusRequest = () => _renameFocus }, Weight: 1f),
            s.SmallPill(_k.T("common.save"), PillKind.Primary, () => CommitRename(idx), RenamePillLabel),
            s.SmallCloseButton(() => _renaming = -1));
        return new BubbleElement(row)
        {
            FillColorFn = () => s.Fill(s.Tile()), OutlineColorFn = () => s.Accent(),
            CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    private void BeginRename(int idx)
    {
        _renaming = idx;
        _confirmRemove = -1;
        _renameBuffer = NameAt(idx);
        _renameFocus++;
    }

    private void CommitRename(int idx)
    {
        if (_renaming != idx) return;   // Enter after Save / ✕ already closed the field
        var name = (_renameBuffer ?? "").Trim();
        _renaming = -1;
        if (name.Length == 0 || name == NameAt(idx)) return;
        _storage.RenameSlot(idx, name);
        _k.Done(_loc.TFormat("layout.fb.renamed", NameAt(idx)));
    }

    private void SwitchSlot(int idx)
    {
        if (idx == _storage.ActiveSlot || idx >= _storage.SlotCount) return;
        _storage.SetActiveSlot(idx);
        _k.Done(_loc.TFormat("layout.fb.switched", NameAt(idx)));
    }

    private void AddSlot()
    {
        if (_storage.SlotCount >= MaxSlots) return;
        _storage.AddSlot();
        _k.Done(_loc.TFormat("layout.fb.added", NameAt(_storage.SlotCount - 1), _storage.SlotCount, MaxSlots));
    }

    private void ConfirmRemove()
    {
        var idx = _confirmRemove;
        _confirmRemove = -1;
        if (idx < 0 || idx >= _storage.SlotCount || _storage.SlotCount <= 2) return;
        var name = NameAt(idx);
        _storage.RemoveSlot(idx);
        _k.Done(_loc.TFormat("layout.fb.removed", name));
    }
}
