# Design-Space Units — refactor plan

**Status: ⛔ CLOSED 2026-07-31 — SUPERSEDED, not started, do not implement as written.**
Scoped 2026-07-31 against `enhance/game-phases` @ `35a4cd7`.

This plan was written to fix UI crispness. It was then established that crispness is a **sprite-baking**
problem, not a units problem (`ui-crispness-plan.md` §1–§3), which removes its motivation. Both topics are
now closed.

⚠️ **§10 is actively wrong and must not be acted on.** It instructs a rewrite of the `WindowBuilder-Patterns.md`
UI-scaling section on the grounds that four recorded rules reverse (`SetScreen`-stays-raw,
`LayoutEditChrome`-stays-at-1.0, the `HudRenderer` token-list blocker, the unit contract). Those reversals hold
only *inside* this model. The model was not adopted, so **the KB rules stand as written** — see the closing
note in that KB doc.

Surviving sections, if resolution-independent layout is ever wanted for its own sake: **§2** (aspect keying on
design width — continuous, rescues 3:2), **§6** (all arithmetic in `UiScaleMath`, since Infrastructure has no
test project), and one present-day fact from **§5** — there is **no `ui.layout.schema` version key anywhere**,
so any future migration run twice silently halves every saved layout. Void: §1, §3, §4, §8, §9, §10.
§7 stays *conditionally* void: its risks (editor hit-tests, `MaxSaneDeltaPx`, threshold drift) are hazards
**created by** this model — today's editor is uniformly screen-px and self-consistent — so they are a reading
list for a re-opening session, not open bugs.

**Problem.** UI is correctly sized at 2160×1440 but far too large at 1280×720, and layout should stay put
across resolutions. Root cause: rects are stored in **screen pixels** while rendering on a **scaled canvas**,
so every boundary needs a conversion — and each conversion has produced a bug (double size-source in
`ClampToScreen`, undersized edit outlines, the documented mixed-unit exception).

---

## 1. The model — take Model B

⚠️ **"Store design units, write them straight to `anchoredPosition`, zero conversions" does NOT work** while
the UI-scale slider multiplies `Canvas.scaleFactor` (`scaleFactor = BaseForHeight(height) × slider`,
`UiScaleMath.cs:36`). If design units *were* canvas units, a slider change would re-scale **positions about
the top-left origin** — windows drift bottom-right and clamp — and the storage key would have to include the
slider.

| | Model A — design == canvas | **Model B — base-normalised (TAKE THIS)** |
|---|---|---|
| `anchoredPosition` | `designPos` | `designPos / slider` |
| `sizeDelta` | `designSize` | `designSize` |
| Conversions left | zero | **one scalar (`1/slider`) at 2 sites**, identity at slider 1.0 |
| Storage key | must be aspect × slider | **aspect only** |
| Slider change | UI scales about origin; windows drift | positions hold screen *fraction*, sizes grow |
| Acceptance test | none | at slider 1.0 every factor is `×1.0` → **bit-identical** |

```
base      = Screen.height / 1440          // UiScaleMath.BaseForHeight — exists
designW   = Screen.width / base = 1440 × aspect
designH   = 1440                          // by construction
screenPos = designPos  × base
screenSize= designSize × base × slider
```

One design space shared by windows, HUD and toasts. The mixed-unit exception **survives but shrinks** — it
stops being about resolution and becomes purely about the slider.

## 2. Aspect keying — key on design WIDTH, not buckets

`designWidth = round(1440 × aspect)` — 2560 @16:9, **2160 @3:2**, 2304 @16:10, 1920 @4:3, 3440 @21:9.
Continuous key: no bucket-boundary discontinuity, the existing nearest-match ports directly (2-D Euclidean →
1-D `|Δ|`, 10% tolerance carries), no enum to maintain, and **it rescues 3:2** — the project's own reference
resolution, which a hand-written bucket list would very likely omit.

⚠️ **Derive the key from `base` only, never `Effective`.** Using the slider-inclusive factor would move the
storage key when the user drags the slider, silently orphaning every saved layout.

## 3. Two KB rules INVERT (both currently recorded as constraints)

- **`LayoutEditChrome` "⛔ must STAY at 1.0" reverses** → it **must** carry `scaleFactor = base`, since it
  draws from rects that are now design units. This fixes the undersized-outline bug for free, one line.
- **"`HudRenderer` has no token list, so HUD scale needs token tracking" is VOID** → that was only true
  because a scale change required re-placing screen-px rects. In design space `anchoredPosition` *is* the
  stored value, so no re-place is needed and the missing token list stops mattering.
- **"⚠️ `SetScreen` must keep reporting RAW screen pixels" also reverses** — see §4.

## 4. Plugin impact — NOT zero

**The condition that keeps 14 plugins unchanged: `IFramework.ScreenWidth/Height` must report DESIGN units**
(`Height` → constant 1440, `Width` → 1440 × aspect). Eight plugins build rects as
`Framework.ScreenWidth - <const>` (MinimalNameplate, Position, MahiruUtility, CustomProfileImage, Experiment,
Maestro ×2, AutoFishing). Left raw, they land mid-screen at 720p.

Change the **semantics**, don't add `DesignWidth`/`DesignHeight` — additive members leave those 8 broken until
each is edited and republished. It is a silent semantic change to a public interface → changelog + rewrite the
XML docs at `IFramework.cs:17,20`.

This also makes the documented HUD idioms *exactly* behaviour-preserving:

| Idiom | @1440p today | @720p today | After |
|---|---|---|---|
| `ScreenHeight / 19` | 75.8 px | 37.9 px | 75.8 design × base = 75.8 / 37.9 px |
| RaidManager `ScreenHeight / 1080f` | 1.333 | 0.667 | 1.333 const × base = identical |

⚠️ **RaidManager only stays correct if `ScreenHeight` and the HUD canvas scale change in the SAME commit.**
Either alone breaks it.

**Four plugins genuinely break** — they mix raw `Input.mousePosition` into a rect that becomes design units.
Silent, resolution-dependent misplacement (fine at 1440p, ~2× off at 720p):
`StellarCooldownBarPlugin\Plugin.Tooltip.cs:74-78` · `StellarEntityInspectorPlugin\Plugin.GearDetail.cs:78-82`
· `StellarCombatMeterPlugin\Plugin.RowMenu.cs:85-98` · `StellarMaestroPlugin\Plugin.Help.cs:28-30` (partial —
uses `Framework.ScreenHeight`, so §4 fixes it; verify only).
Better long-term fix: add a design-space pointer to `IWindowHost` (3/8 members, has room) so plugins stop
reaching for `UnityEngine.Input`.

**Fixed for free:** CooldownBar `Plugin.Seen.cs:29-33` (today mixes canvas units with screen px);
edit-toolbar centring `LayoutEditorOverlay.Toolbar.cs:44` (`res.Width - 1180f` — screen width minus a
canvas-unit constant, already a live bug). CombatMeter's own absolute coords (`Plugin.cs:181-184`) are
*already* wrong off-1440p; after the refactor its 1440p-tuned values become correct everywhere.

## 5. Migration

`designPos = savedScreenPos / base(savedHeight)`, parsed from the existing `WxH` key.

⚠️ **Not exact in general.** Positions were saved via `GetRect`, which multiplies by the **full**
`base × slider`, and **the slider at save time is not recorded**. Exact only when the slider was 1.0. Mitigate
by dividing by `base(savedHeight) × currentSliderFromConfig` — exact unless the user moved the slider since
saving. The slider shipped 2026-07-30, one day before this plan, so essentially nobody has a non-1.0 value.

**Size needs no conversion** — since `30bc727` it is already stored in canvas units, which *are* design units
under Model B.

⚠️⚠️ **There is NO schema-version key anywhere** in `LayoutStorage` or `NativeUiService`. Running the
migration twice divides twice and silently halves every layout. **Add `ui.layout.schema` first, as its own
commit.**

**Collapse rule when several resolutions map to one aspect:** `WindowState` carries **no timestamp** and the
flat-key format has no ordering, so recency is *unknowable*. ⇒ **greatest saved height wins** — deterministic,
explainable, and numerically best (dividing a 2160p position by 1.5 loses less precision than a 720p one by
0.5). Log every collapse.

## 6. Testability rule — no arithmetic in Infrastructure

`Stellar.Infrastructure` has **no test project**. Every conversion goes into `UiScaleMath` (Application, which
does) as a pure static; Infrastructure only calls it. The dependency already exists
(`WindowRenderer.cs:102,277`).

New statics, each tested: `DesignWidth`, `ToDesignPos`/`ToScreenPos`, `ToScreenSize`, `ClampDesignRect`,
`DesignWidthKey`. `ClampDesignRect` replaces the double-size-source bug and must be pinned: at
`base = effective = 1.0` it is bit-identical to today's `ClampVisible`.

## 7. Risks — silent corruption first

1. **⚠️ HIGHEST: editor hit-tests.** `LayoutEditorOverlay.cs:219-229, 231-243, 181` compare a **screen-px
   pointer** against rects that become design units. Correct at 1440p+100%, silently wrong everywhere else —
   clicks select or drag the **wrong element**. Most likely thing to ship broken, precisely because 1440p is
   the dev resolution.
2. **Mixed domains in one list.** `_chromeItems` merges window, HUD **and native game-UI** rects;
   `LayoutEditorService.UpdateDrag` snaps across all three. Native rects are in *game* screen space.
3. **Double migration** — see §5.
4. **`PandaHudAdapter.MaxSaneDeltaPx = 6000f`** (`:302`) is a screen-px sanity cap; fed design deltas it
   mis-triggers (this is the failure that historically flung game HUD elements off-screen).
5. **Threshold drift:** `MinVisiblePx = 80` and `SnapThresholdPx = 6` become design units → 40 px and 3 px at
   720p. Widen or scale them; also rename off `Px`.
6. **Diagnostics lie** — `WindowRenderer.Diagnostics.cs:48-60` prints numbers whose meaning changes.
7. **`WindowCanvasScale`'s doc comment** (`:15-19`) states the OLD contract normatively — rewriting it is part
   of the work, not optional.

### Cannot be settled statically — needs in-game verification
- **Is text legible at 720p?** Design space *entails* an 11 pt design font rendering at ~5.5 px at 720p. The
  slider is the escape hatch; if unacceptable the answer is a **floor on `base`**, not abandoning the model.
- Does the game's own UI scale linearly with height? (gates Stage 3)
- Are content-sized windows design-stable? (`ContentSizeFitter` widths derive from glyph metrics rounded at
  the *scaled* size — likely negligible, unverifiable statically)
- RaidManager's composed scaling — the algebra says exactly preserved; verify visually at 720p/1080p/1440p.

## 8. Jitter interaction — NEUTRAL, do not bundle

The **set of scale factors in play is unchanged** — `2e84a8f` already made `scaleFactor = base × slider`, so
0.5/0.75/1.0/1.5 are live today. The refactor changes what stored numbers *mean*, not what the canvas is
scaled by. It helps only marginally (a window's `anchoredPosition` stops being re-derived from a screen-px
round-trip on every `SetRect`), and does not touch the child-level `pixelPerfect` rounding.

⛔ **Do not bundle the `9e1f9ca` screen-space-accumulator revert into this refactor** — that is a separate
revert of a separately-disproven theory, and conflating them makes any future A/B uninterpretable.

## 9. Staging

**Stage 0 (each ships alone, no behaviour change):** ① add `ui.layout.schema` version key. ② add the
design-space statics to `UiScaleMath` + tests, no callers yet.

**Stage 1 — the core. MUST BE ATOMIC.** It cannot be split: 8 plugins mix `IFramework.ScreenWidth` into window
rects and RaidManager composes `ScreenHeight` with the HUD canvas factor, so canvas scaling and `Screen*`
semantics change together or one regresses.
`WindowCanvasScale` publishes `Base`+`Effective` · `WindowRenderer` Set/GetRect + delete `ClampToScreen` + trim
`SyncUiScaleIfChanged` · ticker `s → Effective` · `FireOnClickWithRect`, `PositionDropdown`, toolbar centring ·
`HudRenderer`+`ToastRenderer` canvas `scaleFactor = base` · `IFramework.Screen*` → design · editor pointer +
native-rect conversions + `LayoutEditChrome` scale · migration behind the schema key · **keep the
per-resolution key for now**.

**Acceptance:** at 2560×1440 slider 100%, every factor is `×1.0` → config byte-identical after migration, no
window moves one pixel. Then at 1280×720 the whole UI is exactly half-size in the same relative position, and
edit outlines box their windows exactly.

**Stage 1b (parallel, independent releases):** the 3 plugin tooltip/menu patches.

**Stage 2 — aspect keying:** `LayoutStorage` key → `DesignWidthKey`; nearest-match → 1-D; collapse with
greatest-height-wins, logged; schema → 3. *Clean seam because Stage 1 already normalised the values, so every
resolution key of a given aspect holds identical numbers — the collapse is provably lossless.*

**Stage 3 (optional, gated on research):** native game UI to design space; retire `NativeUiService`'s second
per-resolution store; re-scale `MaxSaneDeltaPx`.

## 10. Knowledge Base

`WindowBuilder-Patterns.md` needs the UI-scaling section **rewritten, not appended** — the unit contract
flips, the mixed-unit exception narrows to slider-only, trap 3 stays resolved, and **four recorded rules
reverse or void**: `SetScreen`-must-stay-raw, `LayoutEditChrome`-must-stay-at-1.0, the `HudRenderer`
token-list blocker, and the unit contract itself. Leaving those standing would send the next session in
exactly the wrong direction.
