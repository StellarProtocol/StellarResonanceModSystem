using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// The WindowToken captured at build time + its per-tick Apply/Reskin passes. Split out of WindowBuilder.cs for
// the file-size gate; it is the same partial class, so the nested binding types (TextBinding, BarBinding, …,
// in WindowBuilder.Bindings*.cs) resolve without qualification.
internal sealed partial class WindowBuilder
{
    // ---- token + bindings (captured at build; Apply re-pulls dynamic leaves) ----

    public sealed class WindowToken
    {
        public GameObject Root = null!;
        public RectTransform Rect = null!;
        public bool Resizable;   // window's root is fixed-size (grip-resizable) rather than content-height-fit
        internal bool EditModeInteractive;   // window's controls stay clickable during layout-edit mode (toolbar only)
        internal int ZOrder;     // plugin-set explicit draw order (higher = on top); primary stacking key
        internal int ZCat;       // WindowCategory as int (HUD=0<Tools=1<Debug=2) — tiebreak when no explicit front
        internal bool ZPopup;    // click-away popup → always stacked above every Category
        internal int ZSeq;       // mount sequence — final tiebreak within same ZFront tier
        internal int ZFront;     // BringToFront counter — non-zero overrides ZCat/ZSeq; higher = more recently fronted
        private bool _laidOut;   // first structural layout done? (mount = immediate; later per-tick = deferred)

        /// <summary>Re-arm the immediate first-layout path when a hidden window is re-shown, so a content-sized popup
        /// sizes the same frame it reappears instead of one frame at its previous size (deferred rebuild path).</summary>
        internal void ResetLayout() => _laidOut = false;
        internal readonly List<TextBinding> Texts = new();
        internal readonly List<StyledTextBinding> StyledTexts = new();   // TMP real-bold emphasis headers
        internal readonly List<ButtonBinding> Buttons = new();
        internal readonly List<ToggleBinding> Toggles = new();
        internal readonly List<SliderBinding> Sliders = new();
        internal readonly List<XYPadBinding> XYPads = new();   // .XYPad.cs: dot follows Get, enabled dim, square fill
        internal readonly List<UGuiTextInput> Fields = new();
        internal readonly List<FieldBinding> FieldSyncs = new();   // re-sync field text from Get() on external change
        internal readonly List<CondBinding> Conds = new();
        internal readonly List<ListBinding> Lists = new();
        internal readonly List<VirtualListBinding> VirtualLists = new();
        internal readonly List<SpriteBinding> Sprites = new();   // dynamic atlas sub-rect (SpriteElement.UvFunc)
        internal readonly List<IconBinding> Icons = new();       // live tile icon: re-pull bytes, swap texture on change
        internal readonly List<SwatchBinding> Swatches = new();
        internal readonly List<BarBinding> Bars = new();
        internal readonly List<ColorPickerBinding> Pickers = new();
        internal readonly List<FrameOpacityBinding> FrameOpacities = new();
        internal readonly List<HoverBinding> Hovers = new();           // per-apply pin-state poll (visual hover is ticker-driven)
        internal readonly List<SelectableBinding> Selectables = new(); // per-apply selected-state re-tint for SelectableElement rows
        internal readonly List<MeterRowBinding> MeterRows = new();      // per-apply poll for bespoke CombatMeter rows
        internal readonly List<AccentRowBinding> AccentRows = new();    // per-apply poll for role-stripe/share-backdrop rows
        internal readonly List<GameTextureBinding> GameTextures = new();   // per-apply poll for GameTextureElement (sync with VirtualList)
        internal readonly List<CooldownTileBinding> CooldownTiles = new(); // per-apply poll for CooldownBar tiles (icon+fill+seconds+★)
        internal readonly List<ChartBinding> Charts = new();            // per-apply poll: re-mesh LineChart only on series/range change
        internal readonly List<Texture2D> IconTextures = new();        // PNG icons (HideAndDontSave) — reclaimed on destroy
        // Texture dedup for EVERY icon leaf (tiles, button chips, images, sprites, brand logo, pin stars):
        // leaves sharing one PNG byte[] reuse a single uploaded texture (keyed by array reference — plugins
        // hand back stable arrays). Populated and read in ONE place, WindowBuilder.LoadIcon. The texture is
        // owned by IconTextures (added once on first load), so disposal stays single — this map only prevents
        // the duplicate decode+upload, it does NOT own the texture.
        internal readonly Dictionary<byte[], Texture2D> AtlasCache = new();
        internal readonly List<Action<float>> Pulses = new();          // per-frame brand-logo glow pulse (ticker-driven)
        // Re-skin closures captured at build: each re-applies a themed sprite/colour/size from the (rebaked)
        // assets to its existing graphic. Run on a theme change instead of destroying+rebuilding the canvas
        // (which flickered) — uGUI is retained-mode, so in-place re-application is what avoids the 1-frame gap.
        internal readonly List<Action> ReskinActions = new();

        /// <summary>Re-apply the rebaked theme (sprites/colours/sizes) in place, then re-pull values. No GO
        /// destruction → flicker-free live theme switch.</summary>
        public void Reskin()
        {
            for (var i = 0; i < ReskinActions.Count; i++)
            {
                try { ReskinActions[i](); } catch { /* skip a bad leaf; never break the whole re-skin */ }
            }
            Apply();
            // Text sizes (Font Scale) change preferred sizes, but a re-skin doesn't auto-rebuild the layout —
            // rows could collapse (the "all sliders vanish after a font-scale drag; a tab switch brings them
            // back" bug — the tab switch was forcing this rebuild). Force it now.
            if (Rect != null) try { LayoutRebuilder.ForceRebuildLayoutImmediate(Rect); } catch { }
        }

        public void Apply()
        {
            // VirtualLists FIRST: each sets its plugin's window-offset via OnWindow(first) and repositions/
            // activates its pooled rows. Must precede Conds + Texts so slot Funcs (which index snapshot[first+i])
            // and the polymorphic header/picker Conditionals inside each slot read the fresh offset this poll.
            var structuralChange = false;
            for (var i = 0; i < VirtualLists.Count; i++) structuralChange |= VirtualLists[i].Apply();
            // Visibility (Conds/Lists) next so SetActive is settled before value pulls — matches HudElementBuilder.
            // A SetActive that changes which branch/rows are shown also changes the content's preferred size, but
            // uGUI does NOT auto-rebuild a ContentSizeFitter-sized window on a descendant SetActive — so a
            // content-sized window (AutoSizeWidth launcher: Full↔Minimal↔horizontal) would keep its old size and
            // clip/overflow. Force one rebuild when any visibility changed (mirrors Reskin()).
            var vlBuilt = VirtualLists.Count;   // a Cond may lazily build a branch; apply any VirtualList it brought
            for (var i = 0; i < Conds.Count; i++) structuralChange |= Conds[i].Apply();
            for (var i = vlBuilt; i < VirtualLists.Count; i++) structuralChange |= VirtualLists[i].Apply();
            for (var i = 0; i < Lists.Count; i++) structuralChange |= Lists[i].Apply();
            // Force the FIRST layout immediately even with no structural change: width-readback bindings (a
            // FillWidth LineChart reads its laid-out plot width) must see the resolved geometry on their first
            // poll, and a window with no Conditionals/Lists would otherwise leave _laidOut false and the rect
            // unsized until the next render frame. Later per-tick structural changes stay deferred (marked).
            if ((structuralChange || !_laidOut) && Rect != null)
            {
                // The FIRST structural layout (mount) is forced immediate so the window opens correctly sized.
                // Every later per-tick structural change (a Conditional flip / List-count change) only MARKS the
                // layout dirty — Unity coalesces it into its batched canvas rebuild on the next render. This turns
                // the synchronous whole-window ForceRebuild (the ~6ms ChatTools apply spike) into a deferred pass
                // off the Stellar tick. Nothing reads the window size synchronously after Apply (verified), so the
                // one-render-frame settle delay is imperceptible at the 10 Hz apply rate.
                try
                {
                    if (_laidOut) LayoutRebuilder.MarkLayoutForRebuild(Rect);
                    else { LayoutRebuilder.ForceRebuildLayoutImmediate(Rect); _laidOut = true; }
                }
                catch { }
            }
            ApplyValues();
        }

        private void ApplyValues()
        {
            for (var i = 0; i < Texts.Count; i++) Texts[i].Apply();
            for (var i = 0; i < StyledTexts.Count; i++) StyledTexts[i].Apply();
            for (var i = 0; i < Sprites.Count; i++) Sprites[i].Apply();
            for (var i = 0; i < Icons.Count; i++) Icons[i].Apply();
            for (var i = 0; i < GameTextures.Count; i++) GameTextures[i].Apply();
            for (var i = 0; i < Buttons.Count; i++) Buttons[i].Apply();
            for (var i = 0; i < Toggles.Count; i++) Toggles[i].Apply();
            for (var i = 0; i < Sliders.Count; i++) Sliders[i].Apply();
            for (var i = 0; i < XYPads.Count; i++) XYPads[i].Apply();
            for (var i = 0; i < Swatches.Count; i++) Swatches[i].Apply();
            for (var i = 0; i < Bars.Count; i++) Bars[i].Apply();
            for (var i = 0; i < Pickers.Count; i++) Pickers[i].Apply();
            for (var i = 0; i < FrameOpacities.Count; i++) FrameOpacities[i].Apply();
            for (var i = 0; i < Hovers.Count; i++) Hovers[i].Poll?.Invoke();
            for (var i = 0; i < Selectables.Count; i++) Selectables[i].Apply();
            for (var i = 0; i < MeterRows.Count; i++) MeterRows[i].Apply();
            for (var i = 0; i < AccentRows.Count; i++) AccentRows[i].Apply();
            for (var i = 0; i < CooldownTiles.Count; i++) CooldownTiles[i].Apply();
            for (var i = 0; i < Charts.Count; i++) Charts[i].Apply();
            for (var i = 0; i < FieldSyncs.Count; i++) FieldSyncs[i].Apply();
        }

        /// <summary>Re-issue glyph UVs for every Text in the window after the dynamic font's atlas rebuilt.
        /// A shared OS dynamic font (WindowThemeAssets.MenuFont) repacks its atlas when a text-heavy panel
        /// requests many new glyphs; Text built earlier (incl. hidden tabs) keeps stale UVs → garbled glyphs
        /// until refreshed. uGUI auto-refreshes ENABLED tracked text, but hidden-tab text isn't — so we force
        /// ALL of them (GetComponentsInChildren(true) catches title/buttons/labels not in the binding lists).
        /// Only Text on the rebuilt font <paramref name="f"/> is touched — windows hold Text on more than one
        /// dynamic font (MenuFont, and ConfigureText's builtin for HudOverlay text), each with its OWN atlas.
        /// Returns how many Text were refreshed (diagnostic log).</summary>
        public int RefreshFontTexture(Font f)
        {
            if (Root == null) return 0;
            var texts = Root.GetComponentsInChildren<Text>(true);
            var n = 0;
            for (var i = 0; i < texts.Length; i++)
            {
                try { if (texts[i].font == f) { texts[i].FontTextureChanged(); n++; } } catch { }
            }
            return n;
        }

        /// <summary>Destroy native textures the GameObject teardown won't reclaim (the ColorPicker SV/hue
        /// bakes use HideFlags.HideAndDontSave). Call before destroying Root.</summary>
        public void DisposeNativeTextures()
        {
            for (var i = 0; i < Pickers.Count; i++) Pickers[i].Destroy();
            for (var i = 0; i < IconTextures.Count; i++) if (IconTextures[i] != null) UnityEngine.Object.Destroy(IconTextures[i]);
        }

        /// <summary>True while any of this window's text fields holds keyboard focus (drives the
        /// keyboard gate). Cheap; called per tick by WindowService via the renderer.</summary>
        public bool AnyFieldFocused
        {
            get { for (var i = 0; i < Fields.Count; i++) if (Fields[i].IsFocused) return true; return false; }
        }
    }

    // Binding inner-classes (Slider/Text/Button/Toggle/Swatch/Bar/FrameOpacity/Cond/List/Hover/Selectable)
    // live in the sibling partial WindowBuilder.Bindings.cs (split out for the file-size gate).
}
