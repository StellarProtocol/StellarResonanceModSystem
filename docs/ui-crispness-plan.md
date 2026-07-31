# UI crispness — the actual cause, and the cheap fix

**Status: ⛔ CLOSED 2026-07-31 — analysis kept, nothing implemented.** Scoped 2026-07-31 against
`enhance/game-phases` @ `8e8995d`. **Supersedes the crispness parts of `design-space-units-plan.md`** (see §7).

The topic was closed by decision, not because it was finished or disproven: no stage below was built, nothing
was measured in-game, and the "cheap fix" in §3 is an untested hypothesis. The document is retained because
the *diagnosis* is the expensive part — the two corrections in §1 (text was never the problem; the
`[Window/Frac]` sweep was an artifact of reading `rt.position`) are what a re-opening session would otherwise
pay for again. Condensed into `Knowledge Base\WindowBuilder-Patterns.md` §"`scaleFactor` fixes text and breaks
sprites"; that KB entry is the index, this file is the detail.

**If reopened, read §4 first** — the `Capsule`/`SwatchBg` reskin closures are a hard prerequisite for any
rebake, and §7 Stage 0.2 (`mipChain: true`) is the one piece that stands alone and is trivially revertable.

---

## 1. Two wrong explanations, corrected

### ⛔ Text was NEVER the problem
`WindowThemeAssets.MenuFont` is `Font.CreateDynamicFontFromOSFont(...)` (`WindowThemeAssets.cs:91-92`) →
`font.dynamic == true`. For a dynamic font, `Text.pixelsPerUnit` returns `canvas.scaleFactor`, which becomes
`TextGenerationSettings.scaleFactor`, so glyphs are requested at `fontSize × scaleFactor` — **the display
size**. Legacy `UnityEngine.UI.Text` therefore **re-rasterises on every scale change and is already crisp**.

The framework is built around this and says so: `IUiScale.cs:5-7` ("uGUI rasterises dynamic fonts at the
scaled size and text stays crisp"), `WindowRenderer.cs:49-53` `OnFontTextureRebuilt` (exists *because*
scaleFactor changes re-rasterise), `NamedThemeService.cs:157-160` (5% slider quantisation for that reason).

⇒ **TMP is not the fix.** It remains an optimisation only — see §6.

### ⛔ The `[Window/Frac]` sweep result was an ARTIFACT
`WalkFractional` reads `rt.position` (`WindowRenderer.Diagnostics.cs:59`). Children come from
`UGuiPrimitives.NewChild` (`:29-36`) which never sets a pivot → Unity's default `(0.5, 0.5)`. uGUI's
`LayoutGroup.SetChildAlongAxisWithScale` sets **anchors**, never pivot. So `rt.position` is each element's
**centre**, not its rendering origin.

An element with an **odd integer height** whose top edge is exactly pixel-aligned puts its centre on `.5`.
"346 fractional in Y, every one exactly 0.5" is precisely that signature — and the X/Y asymmetry fits, since
`childForceExpandWidth = true` (`WindowBuilder.cs:349-350`) gives siblings one shared width (one X parity)
while heights come from per-element font metrics (mixed parity).

**Voided:** "346 elements cannot render crisp"; "one window-root Y offset inherited by the whole tree".
**Survives:** "only **3** fractional *sizes*" — read from `rt.rect.size`, correct. Derived sizes really are
essentially integral at `base` 1.0, which does void the old plan's fear that they'd block integer layout.

**Fix before trusting any re-run:** test the min corner —
`pos - Vector2.Scale(rt.pivot, rt.rect.size * WindowCanvasScale.Current)` — and note `pos` is screen px while
`rt.rect.size` is canvas units (`:59-60`), which is wrong at any scale ≠ 1.

---

## 2. The real cause: sprites, and specifically 1-texel borders

- `Sprite.Create(..., pixelsPerUnit: 100f, ...)` — `WindowThemeAssets.cs:229, 259`, `HudThemeAssets.cs:84`,
  `ToastThemeAssets.cs:169`.
- **`Canvas.referencePixelsPerUnit` is never assigned anywhere in `src`** → stays at Unity's default `100`.
- ⇒ `Image.pixelsPerUnit = spritePPU / referencePPU = 1` → **1 texel = 1 canvas unit** (which is why
  `FrameRadius = 14` reads as a 14-unit corner).
- The **window canvas is the only canvas with a non-1 `scaleFactor`** (`WindowRenderer.cs:352`, `:109`);
  HUD (`HudRenderer.cs:127-132`), toast (`ToastRenderer.cs:189-191`), edit chrome (`LayoutEditChrome.cs:69-73`)
  and the input blocker are all implicit 1.0.

⇒ At `base` 1.5 a 14-texel arc is bilinear-magnified into 21 px. At 0.75 it is minified with
**`mipChain: false`** (`RoundedTextureBaker.cs:27, 68, 106`; `WindowThemeAssets.cs:202, 237`) → **aliasing**.

**⭐ Prime suspect: `FrameBorderPx = 1` (`WindowThemeAssets.cs:27`) and `BtnBorderPx = 1` (`:32`).** A
one-texel accent ring on every window frame and every button chip. At 1.5 it smears across 1.5 px; at 0.75 it
falls below a pixel and partly dissolves. This one constant likely accounts for most of the perceived softness.

---

## 3. ⭐ The cheap fix — denser bakes + `pixelsPerUnit` compensation

Because `Image.pixelsPerUnit = spritePPU / referencePPU`, bake denser **and** raise the sprite's PPU:

```
e = UiScaleMath.Effective(slider, Screen.height)      // already computed, WindowRenderer.cs:102
bake at   round(design × e) texels   for size, radius, borderPx AND slice border
create as Sprite.Create(..., pixelsPerUnit: 100f * e)
```

Then `Image.pixelsPerUnit = e`, so canvas units = `texels / e` = **the same 14/6/7 footprint as today**.
Layout numbers, `scaleFactor`, `WindowRect` semantics and every plugin are **untouched** — but the sprite is
drawn from `14e` texels into `14e` screen px. **1:1, no stretch.**

At `e = 1.0` every factor is `×1.0` → bit-identical, the repo's standing acceptance criterion.

The codebase already knows the trick: `ToastThemeAssets.cs:38` — *"baked at 2× the 16px on-screen IconSize for
crisp downscale."*

**Verified safe:** no `SetNativeSize` anywhere in `src`; the single `preserveAspect`
(`ToastCardBuilder.cs:208`) is aspect-ratio only, not PPU-dependent.

⇒ **This decouples crispness from the whole native-layout refactor.**

---

## 4. ⚠️ The landmine — rebaking strands `Capsule` / `SwatchBg`

`EnsureNeutralSprites()` / `DestroyThemed()` (`WindowThemeAssets.cs:163-189`) deliberately bake these **once
and never destroy them**. The reason is documented at `:113-118`: slider / toggle / swatch / bar / input
`Image`s hold a **direct sprite reference** with no reskin closure, so destroying the sprite strands them —
the recorded "sliders vanish after a Font Scale slide" bug.

**A scale-driven rebake re-arms that exact bug.** Sprite-reassign reskin closures are needed for ~7 widget
families **first, as their own commit**: slider track/fill/handle (`WindowBuilder.Widgets.cs:214-231`), toggle
track/knob (`:176-184`), swatch (`Preview.cs:24-32`), bar (`Preview.cs:115`), input, `BuildSelectable`
(`Table.cs:50`), dropdown items (`Dropdown.cs:154`).

---

## 5. Rebuild trigger — no remount needed

`SyncUiScaleIfChanged` (`WindowRenderer.cs:98-114`) already re-reads `Screen.height` every poll, so slider
**and** resolution changes share one path. `WindowThemeAssets.Rebake(colors)` + the in-place reskin walk
already exist and are driven by `InvalidateTheme()` (`:119-128`) — **rebake + reskin, no remount**. Reuse it.

Cost is pure managed arithmetic (~166k inside-tests for the frame at `base` 1.5, ×9 sprites, plus `SetPixels`
+ `Apply`) — **low single-digit ms, one-shot**. Fine on slider *release*; **not** fine per drag frame.

⚠️ The existing 5% quantisation (`NamedThemeService.cs:167`) still permits ~30 rebakes across a full
0.5→2.0 drag. Move the bake to **commit-on-release** (`SetUiScale`, `:189-201`), not preview (`:169`).

---

## 6. TMP — defer

Since text is already crisp, TMP is an optimisation. Real benefits: no `Font.textureRebuilt` repack on scale
change (deletes `WindowRenderer.cs:41-53` and `WindowBuilder.cs:266-271`); SDF means `fontSize` needn't be an
`int`, which is the **only** thing that fixes hierarchy collapse at `base` 0.5; material-based outline
replaces the 4-quad `Outline` hack (`WindowBuilder.cs:394-398`).

⛔ **Blocked for now:** the framework has **no way to obtain its own `TMP_FontAsset`** — no `TMP_Settings`,
no `Resources.Load<TMP_FontAsset>`, no `CreateFontAsset` anywhere in `src`. The only mechanism is *borrowing*
off a live native game subtree (`PandaUGuiAdapter.Build.cs:439-448`,
`PandaProfileCardActionInjector.Build.cs:120-123`), and both sites already carry a legacy-`Text` fallback for
when that returns null. Also `WindowBuilder` is explicitly IL2CPP-free and runs headless in the UI sandbox
(`WindowBuilder.cs:10-16`) — there is no game `TMP_Text` to borrow there. Plus glyph coverage (`✕ ▾ ★`) and
`preferredWidth`/`preferredHeight` drift invalidating every tuned constant.

Cheaper first move if the font hierarchy is the complaint: raise/remove `Scaled`'s `Mathf.Max(8, …)` floor
(`WindowBuilder.cs:409`).

---

## 7. Staging

### Stage 0 — hygiene + measurement (each ships alone, no behaviour change)
1. **Fix `WalkFractional`** (min corner + scale `rt.rect.size`); re-run at 1.0 / 1.5 / 0.75. **Gates Stage 2.**
2. **`mipChain: true`** + `Apply(updateMipmaps: true)` at `RoundedTextureBaker.cs:27/51, 68/93, 106/139` and
   `WindowThemeAssets.cs:202/221`. Fixes downscale aliasing outright at `base < 1`. Trivially revertable.
3. **De-duplicate constants** so later scaling applies once, not N times: `Preview.cs:85/98/115` re-hard-code
   `BarPrefixWidth/BarNumericWidth/BarTrackWidth/BarHeight`; `Bindings.cs:457` re-hard-codes `MeterCrestW`;
   scroll gutter `-9f` in `Widgets.cs:274` + `VirtualList.cs:29`; `gap: 2f` in `HudElementBuilder.cs:353` +
   `Layout.cs:64`; body padding `12` is a bare literal at `Chrome.cs:110, 139`.
4. **Reskin closures for `Capsule`/`SwatchBg` holders** (§4). Prerequisite for any rebake.
5. **`UiScaleMath` statics + tests** (Application, which has tests), no callers yet:
   `Px(design, base) = Mathf.Max(1, RoundToInt(design*base))` for hairlines, `Round` for the rest,
   `BakeSize/BakeRadius/BakeBorder`. Pin `base == 1 ⇒ identity`.

### ⭐ Stage 1 — scale-aware baking with PPU compensation (ships alone)
Bake size/radius/border/slice at `round(design × e)`; `Sprite.Create(..., pixelsPerUnit: 100f * e)`. Drive
from `SyncUiScaleIfChanged` reusing `InvalidateTheme`'s body; bake on **commit**, not preview.
**Acceptance:** 1440p/100% pixel-identical; at 2160p the frame border is a hard 2 px with no bilinear halo.
**This is the whole visible fix.**

### Stage 2 — decide whether to continue AT ALL
If Stage 1 makes it crisp, the remaining payoff is **code health** (11 `WindowCanvasScale` conversion sites,
3 documented traps) and the **jitter bug** — not crispness. **Do not pre-commit.**

### Stage 3 (only if Stage 2 is taken) — native layout, ATOMIC
`scaleFactor → 1`; fold `× base` into `Scaled()` (`WindowBuilder.cs:409` — one line covering ~20 sites) and
into every layout constant via `UiScaleMath.Px`; drop the PPU compensation; multiply plugin-supplied
dimensions at the builder read sites; delete the 11 conversions; close the 7 missing `RegisterTextReskin`
gaps; add a `ResizeActions` closure list. Cannot be split — `scaleFactor` and the multiplier must move
together or everything renders at `base²` or `1`.

---

## 8. Key risks

1. **Rebaking strands `Capsule`/`SwatchBg`** — §4. Highest-probability shipped bug.
2. **Hairlines vanish at `base < 1`.** 1-px dividers (`WindowBuilder.cs:436, 442`; `Chrome.cs:228`;
   `MeterRow.cs:301, 482-483`) and the 1-texel sprite borders. Use `Mathf.Max(1, RoundToInt(x*base))` for
   anything ≤ 2 design px. `ChartLineWidth/GridWidth 0.5f` (`LineChart.cs:86-89`) are already sub-pixel.
3. **Sub-pixel bake degeneration at `base` 0.5** — `BtnTexSize 24/radius 6/slice 8` → `12/3/4`;
   `SwatchTexSize 16/border 4` → `8/3`. Assert `2 × slice ≤ size`; floor radii at 2.
4. **Font hierarchy collapse at low `base`** — `Text.fontSize` is an `int`; `Scaled`'s `Mathf.Max(8, …)` floor
   flattens 9/10/11/12/13/14/15 to mostly 8 at 0.5.
5. **Rounding drift does NOT accumulate** — uGUI sums already-integer inputs, so rounding inputs is exact.
   No post-layout pass needed. `RowGap 8`, `SectionGap 12`, `TitleBarHeight 32` are all exact at 0.5/0.75/1.5.
6. **500-LoC pressure** for Stage 3: `Bindings.cs` 497, `MeterRow.cs` 486, `WindowBuilder.cs` 460,
   `WindowRenderer.cs` 413 — split **before**, as separate commits (Session Rule 7).
7. `IWindowRenderer` is at **8/8** — a rebuild trigger can't get its own member; ride `ApplyValues` as
   `SyncUiScaleIfChanged` already does. `IUiScale` shrinking 4→2 (retiring the pixelPerfect toggle) gives
   headroom there instead.

## 9. Must be settled in-game
1. Re-run the **fixed** sweep at 1.0/1.5/0.75 — is there any real sub-pixel placement? **Gates Stage 2.**
2. Are `Text.preferredHeight` / `ContentSizeFitter` sizes integral at `fontSize` 21 and 8 (not just 14)?
3. Does Stage 1 alone visibly fix it? Use the repo's documented method ([[feedback_shimmer_is_no_aa]]):
   5× zoom on a window border silhouette, 1440p vs 2160p, before/after.
4. Rebake cost in ms for 9 textures at `base` 1.5 — under one frame?
5. Does rebaking strand live widgets even with the new closures?
6. `Sprite.Create` with non-100 `pixelsPerUnit` under IL2CPP — do 9-slice borders land exactly?

## 10. Knowledge Base — AMEND, don't rewrite
Contrary to the old plan, these KB rules are **CONFIRMED, not reversed**, for this design: `SetScreen` keeps
reporting raw screen pixels; `LayoutEditChrome` stays at 1.0; the `HudRenderer` token-list constraint stays
live (the HUD is not being scaled); the unit contract and its position-vs-size exception survive with `base`
substituted for `scaleFactor`.

**New and most valuable:** *`Canvas.scaleFactor` fixes text and breaks sprites.* Dynamic-font `Text`
re-rasterises at `fontSize × scaleFactor`; 9-slice sprites are magnified/minified with `mipChain: false`. The
cheap fix is denser bakes plus `pixelsPerUnit: 100 × scaleFactor`.

**New:** `rt.position` on a uGUI child is the **pivot centre** (default `0.5,0.5`; layout groups set anchors,
never pivot). A `.5` fraction with an odd size is *correct* alignment. Never diagnose sub-pixel placement from
`rt.position` without subtracting `pivot × size`.

`docs\design-space-units-plan.md` is **SUPERSEDED (canvas-scaling model)**. Surviving sections: §2 (aspect
keying), §6 (testability), §7.2/4/6/7. Void: §1, §3, §4, §5, §7.1/5, §8.
