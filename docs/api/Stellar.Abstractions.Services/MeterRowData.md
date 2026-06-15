# MeterRowData structure

All data one CombatMeter row needs to paint, in framework-neutral types (no UnityEngine). The plugin computes this from its services each refresh; the framework's `BuildMeterRow` reproduces the bespoke borderless meter row (HP spine, class crest, name·spec·share line, role-coloured metric bar with the per-second/total overlay, self highlight, offline scrim). A plain field struct (no big constructor) so it stays clear of the analyzer's parameter/ctor-dependency caps and mirrors the plugin's former MeterRowVisual.

```csharp
public struct MeterRowData
```

## Public Members

| name | description |
| --- | --- |
| [AbilityScore](MeterRowData/AbilityScore.md) | Ability/combat score text shown as a gold pill after the spec. Empty string when the value is unavailable. |
| [BarFraction](MeterRowData/BarFraction.md) | Metric bar fill fraction in [0..1] normalised to the top-row value. |
| [ClassName](MeterRowData/ClassName.md) | Optional base-class line (e.g. "Frost Mage"); shown between name and spec when ShowClassName. |
| [CrestTexture](MeterRowData/CrestTexture.md) | Opaque icon handle; MUST be a UnityEngine.Texture2D. Passed through to the uGUI image binding; a non-Texture2D silently renders nothing. |
| [CrestUv](MeterRowData/CrestUv.md) | Atlas sub-rect for the class crest icon (normalised UV, bottom-left origin). |
| [Dead](MeterRowData/Dead.md) | True when this entity is dead (HP known and zero) — drives the dead treatment. |
| [HpColor](MeterRowData/HpColor.md) | HP spine fill colour (green/yellow/red by health fraction). |
| [HpFraction](MeterRowData/HpFraction.md) | HP fraction in [0..1] used for the vertical HP spine height. |
| [Id](MeterRowData/Id.md) | Entity this row represents; `default` for an empty raid slot. Used by drag-to-rearrange. |
| [Imagine0](MeterRowData/Imagine0.md) | First equipped Battle Imagine (left trailing icon). [`None`](../Stellar.Abstractions.Domain/ImagineSlot/None.md) when absent. |
| [Imagine1](MeterRowData/Imagine1.md) | Second equipped Battle Imagine (right trailing icon). [`None`](../Stellar.Abstractions.Domain/ImagineSlot/None.md) when absent. |
| [ImaginePosition](MeterRowData/ImaginePosition.md) | Battle-Imagine cluster placement. |
| [ImagineSize](MeterRowData/ImagineSize.md) | Battle-Imagine icon size. |
| [IsLeader](MeterRowData/IsLeader.md) | True when this row represents the party leader — draws a small flag marker before the name. |
| [IsSelf](MeterRowData/IsSelf.md) | True when this row represents the local player — draws the self-highlight tint. |
| [Name](MeterRowData/Name.md) | Character display name (e.g. "Momoko"). |
| [Offline](MeterRowData/Offline.md) | True when this party member is offline — draws a dimmed scrim over the row. |
| [PrimaryValue](MeterRowData/PrimaryValue.md) | Primary metric string drawn on the left of the bar (e.g. per-second DPS). |
| [Rank](MeterRowData/Rank.md) | Rank label rendered before the leader flag (e.g. "1."). |
| [RoleColor](MeterRowData/RoleColor.md) | Role-colour fill for the metric bar (DPS=red, Tank=blue, Healer=green). |
| [SecondaryValue](MeterRowData/SecondaryValue.md) | Secondary metric string drawn on the right of the bar (e.g. total damage). |
| [SharePercent](MeterRowData/SharePercent.md) | Share-of-total percentage string (e.g. "14%"). |
| [ShowAbilityScore](MeterRowData/ShowAbilityScore.md) | When true the ability-score pill is shown (off until the wire field exists). |
| [ShowClassName](MeterRowData/ShowClassName.md) | When true the optional class-name line is shown. |
| [ShowCrest](MeterRowData/ShowCrest.md) | When false the class crest cell is hidden (and reclaims its width). |
| [ShowHpBar](MeterRowData/ShowHpBar.md) | When false the HP spine is hidden. |
| [ShowImagine](MeterRowData/ShowImagine.md) | When false the whole Battle-Imagine cluster is hidden. |
| [ShowImagineCooldown](MeterRowData/ShowImagineCooldown.md) | When false the cooldown-seconds label beside the Imagine icon is hidden (the radial sweep on the icon stays). User toggle; also suppressed automatically at raid-20 density. |
| [ShowLeaderFlag](MeterRowData/ShowLeaderFlag.md) | When false the leader flag marker is hidden even for the leader. |
| [ShowPrimary](MeterRowData/ShowPrimary.md) | When false the per-second (primary) value overlay is hidden. |
| [ShowRank](MeterRowData/ShowRank.md) | When false the rank label is hidden. |
| [ShowSecondary](MeterRowData/ShowSecondary.md) | Width-driven collapse flag: when false the secondary metric value is hidden. |
| [ShowShare](MeterRowData/ShowShare.md) | Width-driven collapse flag: when false the share-percent label is hidden. |
| [ShowSpec](MeterRowData/ShowSpec.md) | Width-driven collapse flag: when false the Spec label is hidden to save horizontal space. |
| [Spec](MeterRowData/Spec.md) | Sub-profession label; may be empty when the spec is unknown or hidden. |

## See Also

* namespace [Stellar.Abstractions.Services](../Stellar.Abstractions.md)

<!-- DO NOT EDIT: generated by xmldocmd for Stellar.Abstractions.dll -->
