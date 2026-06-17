# Changelog

All notable changes to the Stellar framework are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.1.0] - 2026-06-18
### Added
- `LineChartElement` — multi-series time-series line chart with labelled X/Y axes, axis titles, a
  legend, auto-scaled Y, and interactive zoom (scroll/drag + −/+/Reset buttons + range scrollbar).
  Rendered via an injected `MaskableGraphic` mesh (`ChartGraphic`). First consumer: CombatMeter
  history charts.
- Generic inter-plugin exchange: `IPluginExchange` (`Provide<T>` / `Consume<T>`) on `IPluginServices`,
  brokered purely by `Type`, plus the new **`Stellar.PluginContracts`** assembly that cooperating
  plugins reference (alongside `Stellar.Abstractions`) for shared contracts — `FrozenEntity` and
  `IFrozenEntityViewer`. (#5 Phase 2)
- HUD text enhancements on `TextElement`: `FontSize`, `DynamicFontSize`, `ShadowDistance`, and
  `NoWrap`, plus `TextAlign` (Left/Center/Right). Existing 6-argument call sites stay source- and
  binary-compatible via explicit secondary constructors.
- HUD centering anchors `HudAnchor.ScreenCenterX` / `ScreenCenterY` / `ScreenCenter` (resolution-
  independent via the RectTransform anchor system) and `HudSpec.DynamicDefaultRect`.
- `IFramework.ScreenWidth` / `ScreenHeight`, refreshed once per frame, for resolution-aware layout.
- `ICombatSpec` combat-spec lookup; `MeterRowData.SelfAccent`.
- Buff/skill `NameDesign` fallback; leveled `skill_level_ids` now resolve to their base skill names.
### Changed
- Icon-only overlay buttons are centered and equal-size.
### Fixed
- Hotkey handling, layout-editor input leak, combat-spec resolution, and theme-name sync (in-game
  hotfix batch, #2).
- Programmatic `SetRect` is clamped on-screen so windows can't be placed unreachable.
- `BarElement` fill width now tracks `Fraction01`.

## [1.0.1] - 2026-06-17
### Fixed
- Per-frame `GameObject.Find` leak that decayed FPS across dungeon re-entries.

## [1.0.0] - 2026-06-08
### Added
- In-game plugin launcher overlay
### Fixed
- Party roster for raid groups

## [0.9.0] - 2026-05-29
### Added
- Native HUD element editor (move / hide)
- PlayerHUD demonstrator
