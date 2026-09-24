using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder support for the meter row's trailing 2×2 debuff block (up to 4 debuff cells at the row's far
/// right). Each cell matches the CooldownBar tile style: a red accent frame, a dark inset body, a BRIGHT icon
/// (game-asset art — never scrimmed, so it stays readable), a foot fill-bar showing time remaining, and an
/// optional ×N stacks badge; the 4th cell renders a "+N" overflow count when a member has more than four
/// debuffs. Empty cells render a faint placeholder square (never an empty void). Fixed 42px block (two 20px
/// cells + a 2px gap) via a <see cref="GridLayoutGroup"/>, so <see cref="MeterRowBinding"/> reserves matching
/// content padding and every row's metric bar ends at the same x. Built once per row; poll-diffed via
/// <see cref="BindDebuffBlock"/>. Imagine-lockout debuffs (e.g. Time Stasis) carry the source imagine's card as
/// their icon (resolved plugin-side).
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float MeterDebuffCell  = 20f;                                  // cell square edge (px) — bigger icons
    private const float MeterDebuffGap   = 2f;                                   // inter-cell gap (px) — tight
    private const float MeterDebuffBlock = MeterDebuffCell * 2f + MeterDebuffGap; // 42px fixed 2×2 block edge

    private static readonly Color MeterDebuffFrame = new(0.88f, 0.32f, 0.29f, 0.95f); // red cell accent frame
    private static readonly Color MeterDebuffInset = new(0f, 0f, 0f, 0.95f);          // dark tile body behind the icon
    private static readonly Color MeterDebuffBg    = new(0.10f, 0.06f, 0.07f, 1f);    // backing while art loads / is unavailable
    private static readonly Color MeterDebuffFill  = new(0.90f, 0.36f, 0.33f, 0.95f); // foot fill-bar (time remaining)
    private static readonly Color MeterDebuffMore  = new(0.90f, 0.78f, 0.42f, 1f);    // "+N" gold
    private static readonly Color MeterDebuffEmpty = new(0.58f, 0.63f, 0.72f, 0.20f); // faint filled square for an EMPTY slot placeholder

    // Handles for one debuff cell — owned by the row, mutated by the binding.
    internal sealed class DebuffCell
    {
        public GameObject Root = null!;
        public Image Frame = null!;         // accent frame (red for a debuff, faint for an empty slot)
        public GameObject InsetGo = null!;  // dark tile body (hidden for an empty slot)
        public RawImage Art = null!;        // the icon — always bright, never scrimmed
        public RectTransform FillRt = null!;// foot fill-bar rect (width = time-remaining fraction)
        public Image FillImg = null!;
        public Text Stacks = null!;
        public GameObject StacksGo = null!;
        public Text More = null!;
        public GameObject MoreGo = null!;
    }

    // The fixed 2×2 block (4 cells).
    internal sealed class DebuffBlock
    {
        public GameObject Root = null!;
        public GridLayoutGroup Grid = null!;
        public LayoutElement Le = null!;
        public DebuffCell[] Cells = new DebuffCell[4];
    }

    // Per-block poll-diff cache (block-level show flag; per-cell state is cheap enough to re-poll).
    internal struct DebuffBlockCache { public bool Init, Shown; public float Size; }

    // Build the fixed 2×2 block: a GridLayoutGroup pinned to 42px. Root inactive until ShowDebuffs.
    private DebuffBlock BuildDebuffBlock(WindowToken token, Transform host)
    {
        var root = UGuiPrimitives.NewChild("Debuffs", host);
        var grid = root.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(MeterDebuffCell, MeterDebuffCell);
        grid.spacing = new Vector2(MeterDebuffGap, MeterDebuffGap);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 2;
        grid.childAlignment = TextAnchor.MiddleCenter;
        var le = root.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = MeterDebuffBlock;
        le.preferredHeight = MeterDebuffBlock;

        var block = new DebuffBlock { Root = root, Grid = grid, Le = le };
        for (var i = 0; i < 4; i++) block.Cells[i] = BuildDebuffCell(token, root.transform);
        root.SetActive(false);
        return block;
    }

    // One debuff cell (CooldownBar tile style): red frame > dark inset > bright icon + foot fill-bar; stacks + "+N".
    private DebuffCell BuildDebuffCell(WindowToken token, Transform parent)
    {
        var cellGo = UGuiPrimitives.NewChild("D", parent);
        var frame = cellGo.AddComponent<Image>(); frame.color = MeterDebuffFrame; frame.raycastTarget = false;

        // Dark inset body (1px in from the frame) — the icon and fill-bar live inside it.
        var insetGo = UGuiPrimitives.NewChild("Inset", cellGo.transform);
        var insetImg = insetGo.AddComponent<Image>(); insetImg.color = MeterDebuffInset; insetImg.raycastTarget = false;
        var insetRt = insetGo.GetComponent<RectTransform>();
        insetRt.anchorMin = Vector2.zero; insetRt.anchorMax = Vector2.one;
        insetRt.offsetMin = new Vector2(1f, 1f); insetRt.offsetMax = new Vector2(-1f, -1f);

        var artGo = UGuiPrimitives.NewChild("Art", insetGo.transform);
        UGuiPrimitives.Stretch(artGo);
        var art = artGo.AddComponent<RawImage>(); art.raycastTarget = false; art.color = Color.white;

        // Foot fill-bar (bottom, width = remaining fraction via anchorMax.x — sprite-less, anchor-sized).
        var fillGo = UGuiPrimitives.NewChild("Fill", insetGo.transform);
        var fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = new Vector2(0f, 0f); fillRt.anchorMax = new Vector2(1f, 0f); fillRt.pivot = new Vector2(0f, 0f);
        fillRt.sizeDelta = new Vector2(0f, 3f); fillRt.anchoredPosition = Vector2.zero;
        var fillImg = fillGo.AddComponent<Image>(); fillImg.color = MeterDebuffFill; fillImg.raycastTarget = false;

        var stkGo = UGuiPrimitives.NewChild("Stk", cellGo.transform);
        var stkRt = stkGo.GetComponent<RectTransform>();
        stkRt.anchorMin = stkRt.anchorMax = stkRt.pivot = new Vector2(1f, 1f);   // top-right
        stkRt.sizeDelta = new Vector2(13f, 12f); stkRt.anchoredPosition = new Vector2(2f, 2f);
        var stkBg = stkGo.AddComponent<Image>(); stkBg.color = new Color(0.04f, 0.05f, 0.07f, 0.92f); stkBg.raycastTarget = false;
        var stkTxtGo = UGuiPrimitives.NewChild("T", stkGo.transform); UGuiPrimitives.Stretch(stkTxtGo);
        var stacks = stkTxtGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(stacks, Scaled(9), TextAnchor.MiddleCenter, bold: true);
        ApplyMenuFont(stacks); stacks.color = new Color(1f, 0.86f, 0.4f, 1f);
        RegisterTextSizeReskin(token, stacks, 9);

        var more = AddOverlayText(token, cellGo.transform, "More", TextAnchor.MiddleCenter, baseSize: 10);
        more.color = MeterDebuffMore;

        stkGo.SetActive(false); more.gameObject.SetActive(false);
        return new DebuffCell
        {
            Root = cellGo, Frame = frame, InsetGo = insetGo, Art = art, FillRt = fillRt, FillImg = fillImg,
            Stacks = stacks, StacksGo = stkGo, More = more, MoreGo = more.gameObject,
        };
    }

    // The per-cell edge / whole-block edge for a row's chosen debuff size (0 = the 20px default).
    internal static float DebuffCellPx(in MeterRowData d) => d.DebuffCellSize > 0f ? d.DebuffCellSize : MeterDebuffCell;
    internal static float DebuffBlockPx(in MeterRowData d) => DebuffCellPx(d) * 2f + MeterDebuffGap;

    // Poll-diff the whole block from MeterRowData. Called from MeterRowBinding.ApplyDebuffs.
    private static void BindDebuffBlock(DebuffBlock block, in MeterRowData d, ref DebuffBlockCache cache)
    {
        if (block.Root == null) return;
        if (!cache.Init || d.ShowDebuffs != cache.Shown) { block.Root.SetActive(d.ShowDebuffs); cache.Shown = d.ShowDebuffs; cache.Init = true; }
        if (!d.ShowDebuffs) return;
        var cellPx = DebuffCellPx(d);
        if (!Mathf.Approximately(cellPx, cache.Size))
        {
            var blockPx = DebuffBlockPx(d);
            block.Grid.cellSize = new Vector2(cellPx, cellPx);
            block.Le.preferredWidth = block.Le.minWidth = blockPx;
            block.Le.preferredHeight = blockPx;
            cache.Size = cellPx;
        }
        BindDebuffCell(block.Cells[0], d.Debuff0, 0);
        BindDebuffCell(block.Cells[1], d.Debuff1, 0);
        BindDebuffCell(block.Cells[2], d.Debuff2, 0);
        BindDebuffCell(block.Cells[3], d.Debuff3, d.DebuffOverflow);   // 4th cell owns the "+N" overflow
    }

    private static void BindDebuffCell(DebuffCell cell, in DebuffSlot slot, int overflow)
    {
        if (cell.Root == null) return;
        var more = overflow > 0;
        var present = !more && slot.Present;
        // Every cell renders (the block is shown): a debuff tile, a "+N" count, or a faint empty-slot square.
        cell.Root.SetActive(true);
        cell.Frame.color = present ? MeterDebuffFrame : MeterDebuffEmpty;
        cell.InsetGo.SetActive(present);          // dark body only under a real tile; empty slot = the faint frame alone
        cell.MoreGo.SetActive(more);
        if (more) { cell.More.text = "+" + overflow; cell.StacksGo.SetActive(false); return; }
        if (!present) { cell.StacksGo.SetActive(false); return; }   // empty placeholder slot
        // Bright icon — never scrimmed, so it stays readable (owner: the radial scrim made it "barely see").
        cell.Art.texture = slot.IconTexture as Texture;
        cell.Art.uvRect = new Rect(slot.IconUv.X, slot.IconUv.Y, slot.IconUv.W, slot.IconUv.H);
        cell.Art.color = slot.IconTexture == null ? MeterDebuffBg : Color.white;
        // Foot fill-bar width = time remaining (shrinks as the debuff expires); hidden for a permanent debuff.
        var remain = Mathf.Clamp01(slot.RemainFraction);
        var perm = remain >= 0.999f;   // permanent (or just applied) — a full bar reads as clutter, so hide it
        cell.FillImg.enabled = !perm;
        cell.FillRt.anchorMax = new Vector2(perm ? 0f : remain, 0f);
        var showStk = slot.Stacks > 1;
        cell.StacksGo.SetActive(showStk);
        if (showStk) cell.Stacks.text = slot.Stacks.ToString();
    }
}
