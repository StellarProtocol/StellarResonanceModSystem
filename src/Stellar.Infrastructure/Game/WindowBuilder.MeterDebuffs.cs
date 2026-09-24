using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder support for the meter row's trailing 2×2 debuff block (up to 4 debuff cells at the row's far
/// right). Each cell = a 1px red frame + icon art (<see cref="RawImage"/>) + a Filled/Radial360 duration sweep
/// (dark wedge = elapsed) + an optional ×N stacks badge; the 4th cell renders a "+N" overflow count when a
/// member has more than four debuffs. Fixed 34px block (two 15px cells + a 4px gap) via a
/// <see cref="GridLayoutGroup"/>, so <see cref="MeterRowBinding"/> reserves matching content padding and every
/// row's metric bar ends at the same x. Built once per row; poll-diffed via <see cref="BindDebuffBlock"/>.
/// Mirrors the Battle-Imagine cell recipe in <see cref="BuildImagineCell"/>/<see cref="BindImagineCell"/>.
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float MeterDebuffCell  = 15f;                                  // cell square edge (px)
    private const float MeterDebuffGap   = 4f;                                   // inter-cell gap (px)
    private const float MeterDebuffBlock = MeterDebuffCell * 2f + MeterDebuffGap; // 34px fixed 2×2 block edge

    private static readonly Color MeterDebuffFrame = new(0.88f, 0.32f, 0.29f, 0.90f); // red cell frame
    private static readonly Color MeterDebuffBg    = new(0.14f, 0.08f, 0.09f, 1f);    // dark backing while art loads / is unavailable
    private static readonly Color MeterDebuffScrim = new(0f, 0f, 0f, 0.60f);          // dark overlay on the elapsed arc
    private static readonly Color MeterDebuffMore  = new(0.90f, 0.78f, 0.42f, 1f);    // "+N" gold

    // Handles for one debuff cell — owned by the row, mutated by the binding.
    internal sealed class DebuffCell
    {
        public GameObject Root = null!;
        public RawImage Art = null!;
        public Image Sweep = null!;
        public Text Stacks = null!;
        public GameObject StacksGo = null!;
        public Text More = null!;
        public GameObject MoreGo = null!;
    }

    // The fixed 2×2 block (4 cells).
    internal sealed class DebuffBlock
    {
        public GameObject Root = null!;
        public DebuffCell[] Cells = new DebuffCell[4];
    }

    // Per-block poll-diff cache (block-level show flag; per-cell state is cheap enough to re-poll).
    internal struct DebuffBlockCache { public bool Init, Shown; }

    // Build the fixed 2×2 block: a GridLayoutGroup pinned to 34px. Root inactive until ShowDebuffs.
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

        var block = new DebuffBlock { Root = root };
        for (var i = 0; i < 4; i++) block.Cells[i] = BuildDebuffCell(token, root.transform);
        root.SetActive(false);
        return block;
    }

    // One debuff cell: red frame (root Image) + 1px-inset icon Art + Radial360 sweep + stacks badge + "+N".
    private DebuffCell BuildDebuffCell(WindowToken token, Transform parent)
    {
        var cellGo = UGuiPrimitives.NewChild("D", parent);
        var frame = cellGo.AddComponent<Image>(); frame.color = MeterDebuffFrame; frame.raycastTarget = false;

        var artGo = UGuiPrimitives.NewChild("Art", cellGo.transform);
        var art = artGo.AddComponent<RawImage>(); art.raycastTarget = false; art.color = MeterDebuffBg;
        var artRt = artGo.GetComponent<RectTransform>();
        artRt.anchorMin = Vector2.zero; artRt.anchorMax = Vector2.one;
        artRt.offsetMin = new Vector2(1f, 1f); artRt.offsetMax = new Vector2(-1f, -1f);   // 1px red frame shows through

        var sweepGo = UGuiPrimitives.NewChild("Sweep", cellGo.transform);
        var srt = sweepGo.GetComponent<RectTransform>();
        srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
        srt.offsetMin = new Vector2(1f, 1f); srt.offsetMax = new Vector2(-1f, -1f);
        var sweep = sweepGo.AddComponent<Image>();
        sweep.sprite = CooldownSprite(); sweep.color = MeterDebuffScrim; sweep.raycastTarget = false;
        sweep.type = Image.Type.Filled; sweep.fillMethod = Image.FillMethod.Radial360;
        sweep.fillOrigin = 2 /* Origin360.Top */; sweep.fillClockwise = true; sweep.fillAmount = 0f;

        var stkGo = UGuiPrimitives.NewChild("Stk", cellGo.transform);
        var stkRt = stkGo.GetComponent<RectTransform>();
        stkRt.anchorMin = stkRt.anchorMax = stkRt.pivot = new Vector2(1f, 1f);   // top-right
        stkRt.sizeDelta = new Vector2(11f, 10f); stkRt.anchoredPosition = new Vector2(2f, 2f);
        var stkBg = stkGo.AddComponent<Image>(); stkBg.color = new Color(0.04f, 0.05f, 0.07f, 0.92f); stkBg.raycastTarget = false;
        var stkTxtGo = UGuiPrimitives.NewChild("T", stkGo.transform); UGuiPrimitives.Stretch(stkTxtGo);
        var stacks = stkTxtGo.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(stacks, Scaled(8), TextAnchor.MiddleCenter, bold: true);
        ApplyMenuFont(stacks); stacks.color = new Color(1f, 0.86f, 0.4f, 1f);
        RegisterTextSizeReskin(token, stacks, 8);

        var more = AddOverlayText(token, cellGo.transform, "More", TextAnchor.MiddleCenter, baseSize: 9);
        more.color = MeterDebuffMore;

        stkGo.SetActive(false); more.gameObject.SetActive(false);
        return new DebuffCell { Root = cellGo, Art = art, Sweep = sweep, Stacks = stacks, StacksGo = stkGo, More = more, MoreGo = more.gameObject };
    }

    // Poll-diff the whole block from MeterRowData. Called from MeterRowBinding.ApplyDebuffs.
    private static void BindDebuffBlock(DebuffBlock block, in MeterRowData d, ref DebuffBlockCache cache)
    {
        if (block.Root == null) return;
        if (!cache.Init || d.ShowDebuffs != cache.Shown) { block.Root.SetActive(d.ShowDebuffs); cache.Shown = d.ShowDebuffs; cache.Init = true; }
        if (!d.ShowDebuffs) return;
        BindDebuffCell(block.Cells[0], d.Debuff0, 0);
        BindDebuffCell(block.Cells[1], d.Debuff1, 0);
        BindDebuffCell(block.Cells[2], d.Debuff2, 0);
        BindDebuffCell(block.Cells[3], d.Debuff3, d.DebuffOverflow);   // 4th cell owns the "+N" overflow
    }

    private static void BindDebuffCell(DebuffCell cell, in DebuffSlot slot, int overflow)
    {
        if (cell.Root == null) return;
        var more = overflow > 0;
        var has = more || slot.Present;
        cell.Root.SetActive(has);
        if (!has) return;
        cell.MoreGo.SetActive(more);
        cell.Art.gameObject.SetActive(!more);
        cell.Sweep.gameObject.SetActive(!more);
        if (more) { cell.More.text = "+" + overflow; cell.StacksGo.SetActive(false); return; }
        cell.Art.texture = slot.IconTexture as Texture;
        cell.Art.uvRect = new Rect(slot.IconUv.X, slot.IconUv.Y, slot.IconUv.W, slot.IconUv.H);
        cell.Art.color = slot.IconTexture == null ? MeterDebuffBg : Color.white;
        cell.Sweep.fillAmount = Mathf.Clamp01(1f - slot.RemainFraction);   // dark = elapsed
        var showStk = slot.Stacks > 1;
        cell.StacksGo.SetActive(showStk);
        if (showStk) cell.Stacks.text = slot.Stacks.ToString();
    }
}
