using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// All data one CombatMeter row needs to paint, in framework-neutral types (no UnityEngine). The plugin
/// computes this from its services each refresh; the framework's <c>BuildMeterRow</c> reproduces the bespoke
/// borderless meter row (HP spine, class crest, name·spec·share line, role-coloured metric bar with the
/// per-second/total overlay, self highlight, offline scrim). A plain field struct (no big constructor) so it
/// stays clear of the analyzer's parameter/ctor-dependency caps and mirrors the plugin's former MeterRowVisual.
/// </summary>
public struct MeterRowData
{
    /// <summary>Entity this row represents; <c>default</c> for an empty raid slot. Used by drag-to-rearrange.</summary>
    public EntityId Id;
    /// <summary>Rank label rendered before the leader flag (e.g. "1.").</summary>
    public string Rank;
    /// <summary>Character display name (e.g. "Momoko").</summary>
    public string Name;
    /// <summary>Sub-profession label; may be empty when the spec is unknown or hidden.</summary>
    public string Spec;
    /// <summary>Primary metric string drawn on the left of the bar (e.g. per-second DPS).</summary>
    public string PrimaryValue;
    /// <summary>Secondary metric string drawn on the right of the bar (e.g. total damage).</summary>
    public string SecondaryValue;
    /// <summary>Share-of-total percentage string (e.g. "14%").</summary>
    public string SharePercent;
    /// <summary>Role-colour fill for the metric bar (DPS=red, Tank=blue, Healer=green).</summary>
    public ColorRgba RoleColor;
    /// <summary>HP spine fill colour (green/yellow/red by health fraction).</summary>
    public ColorRgba HpColor;
    /// <summary>Optional override for the name-text colour (e.g. a ready-check vote: blue=pending,
    /// green=ready, red=declined). When alpha is 0 (the default) the row uses the framework's
    /// standard name colour. The <see cref="Dead"/> treatment still takes precedence.</summary>
    public ColorRgba NameColor;
    /// <summary>Self-row highlight colour (background tint + a brighter border), used only when <see cref="IsSelf"/>.
    /// Supplied by the meter so the highlight is a configurable colour slot rather than a fixed framework teal.</summary>
    public ColorRgba SelfAccent;
    /// <summary>HP fraction in [0..1] used for the vertical HP spine height.</summary>
    public float HpFraction;
    /// <summary>Shield overlay fraction in [0..1], drawn as a grey/white band OVER the green HP fill from the
    /// bottom of the vertical spine (shield / maxHp, already clamped by the plugin to <see cref="HpFraction"/>).
    /// 0 (the default) hides the overlay, so a spine renders byte-identical to pre-shield behaviour. Only
    /// meaningful when the spine is in HP mode; the plugin passes 0 in DPS/Off spine modes. Additive field —
    /// a plugin built before it existed leaves it 0.</summary>
    public float HpShieldFraction;
    /// <summary>Metric bar fill fraction in [0..1] normalised to the top-row value.</summary>
    public float BarFraction;
    /// <summary>Shield overlay fraction in [0..1] for the MAIN horizontal bar, drawn as a grey/white band OVER
    /// the fill from the bar's left edge — the horizontal analogue of <see cref="HpShieldFraction"/> on the
    /// spine (shield / maxHp, already clamped by the plugin to <see cref="BarFraction"/>). Only meaningful when
    /// the main bar is in HP mode; the plugin passes 0 in DPS mode, so the bar renders byte-identical to
    /// pre-shield behaviour. Additive field — a plugin built before it existed leaves it 0.</summary>
    public float BarShieldFraction;
    /// <summary>Opaque icon handle; MUST be a UnityEngine.Texture2D. Passed through to the uGUI image binding; a non-Texture2D silently renders nothing.</summary>
    public object? CrestTexture;
    /// <summary>Atlas sub-rect for the class crest icon (normalised UV, bottom-left origin).</summary>
    public UvRect CrestUv;
    /// <summary>Tint multiplier for the class crest image. <c>default</c> (alpha 0) means no tint
    /// (white). Used e.g. to colour the crest by team-voice mic status: red=muted, blue=speaker,
    /// green=talking.</summary>
    public ColorRgba CrestTint;
    /// <summary>True when this row represents the local player — draws the self-highlight tint.</summary>
    public bool IsSelf;
    /// <summary>True when this row represents the party leader — draws a small flag marker before the name.</summary>
    public bool IsLeader;
    /// <summary>True when this party member is offline — draws a dimmed scrim over the row.</summary>
    public bool Offline;
    /// <summary>Width-driven collapse flag: when false the Spec label is hidden to save horizontal space.</summary>
    public bool ShowSpec;
    /// <summary>Width-driven collapse flag: when false the secondary metric value is hidden.</summary>
    public bool ShowSecondary;
    /// <summary>Width-driven collapse flag: when false the share-percent label is hidden.</summary>
    public bool ShowShare;
    /// <summary>First equipped Battle Imagine (left trailing icon). <see cref="ImagineSlot.None"/> when absent.</summary>
    public ImagineSlot Imagine0;
    /// <summary>Second equipped Battle Imagine (right trailing icon). <see cref="ImagineSlot.None"/> when absent.</summary>
    public ImagineSlot Imagine1;
    /// <summary>Debuff cells for the trailing 2×2 block (top-left, top-right, bottom-left, bottom-right). <see cref="DebuffSlot.None"/> when empty. Hidden unless <see cref="ShowDebuffs"/>.</summary>
    public DebuffSlot Debuff0;
    /// <summary>Second debuff cell. See <see cref="Debuff0"/>.</summary>
    public DebuffSlot Debuff1;
    /// <summary>Third debuff cell. See <see cref="Debuff0"/>.</summary>
    public DebuffSlot Debuff2;
    /// <summary>Fourth debuff cell. When <see cref="DebuffOverflow"/> &gt; 0 the LAST visible cell renders a "+N" count instead of an icon.</summary>
    public DebuffSlot Debuff3;
    /// <summary>Fifth..eighth cells, used only when <see cref="DebuffColumns"/> &gt; 2 (a wider block). See <see cref="Debuff0"/>.</summary>
    public DebuffSlot Debuff4;
    /// <summary>See <see cref="Debuff4"/>.</summary>
    public DebuffSlot Debuff5;
    /// <summary>See <see cref="Debuff4"/>.</summary>
    public DebuffSlot Debuff6;
    /// <summary>See <see cref="Debuff4"/>.</summary>
    public DebuffSlot Debuff7;
    /// <summary>Ninth..twelfth cells, used only when <see cref="DebuffColumns"/> &gt; 4. See <see cref="Debuff4"/>.</summary>
    public DebuffSlot Debuff8;
    /// <summary>See <see cref="Debuff8"/>.</summary>
    public DebuffSlot Debuff9;
    /// <summary>See <see cref="Debuff8"/>.</summary>
    public DebuffSlot Debuff10;
    /// <summary>See <see cref="Debuff8"/>.</summary>
    public DebuffSlot Debuff11;
    /// <summary>Number of COLUMNS in the status-effect block (block is 2 rows tall). 0 or 2 = the default 2×2
    /// (4 cells); 3 = 6 cells; 4 = 8; 5 = 10; 6 = 12. A per-mode user option — wider rows (party-5) can show
    /// more. More columns widen the block (narrowing the metric bar) but never change the row HEIGHT.</summary>
    public int DebuffColumns;
    /// <summary>When &gt; 0, the member has more effects than the visible cells hold and the LAST cell shows "+N" (N = this value). 0 = no overflow.</summary>
    public int DebuffOverflow;
    /// <summary>When true the trailing 2×2 debuff block is shown (per-mode user toggle). Default false so plugins built before this field render unchanged.</summary>
    public bool ShowDebuffs;
    /// <summary>Debuff cell edge in px (each of the 2×2 cells). 0 = the renderer default (20px). Larger values
    /// grow the block AND the row height so the bigger icons fit — a per-mode user option.</summary>
    public float DebuffCellSize;
    /// <summary>When false the cooldown-seconds label beside the Imagine icon is hidden (the radial sweep on the icon stays). User toggle; also suppressed automatically at raid-20 density.</summary>
    public bool ShowImagineCooldown;
    /// <summary>When false the rank label is hidden.</summary>
    public bool ShowRank;
    /// <summary>When false the class crest cell is hidden (and reclaims its width).</summary>
    public bool ShowCrest;
    /// <summary>When false the HP spine is hidden.</summary>
    public bool ShowHpBar;
    /// <summary>Width of the vertical spine bar in pixels. 0 = use the renderer default (3 px).</summary>
    public float SpineWidth;
    /// <summary>Obsolete: use <see cref="SpineWidth"/>.</summary>
    [System.Obsolete("Use SpineWidth instead.")]
    public float HpBarWidth { get => SpineWidth; set => SpineWidth = value; }
    /// <summary>When false the per-second (primary) value overlay is hidden.</summary>
    public bool ShowPrimary;
    /// <summary>When false the whole Battle-Imagine cluster is hidden.</summary>
    public bool ShowImagine;
    /// <summary>When false the leader flag marker is hidden even for the leader.</summary>
    public bool ShowLeaderFlag;
    /// <summary>Optional base-class line (e.g. "Frost Mage"); shown between name and spec when ShowClassName.</summary>
    public string ClassName;
    /// <summary>When true the optional class-name line is shown.</summary>
    public bool ShowClassName;
    /// <summary>Ability/combat score text shown as a gold pill after the spec. Empty string when the value is unavailable.</summary>
    public string AbilityScore;
    /// <summary>When true the ability-score pill is shown (off until the wire field exists).</summary>
    public bool ShowAbilityScore;
    /// <summary>True when this entity is dead (HP known and zero) — drives the dead treatment.</summary>
    public bool Dead;
    /// <summary>Optional small status icon shown on the name line (e.g. team-voice mic/headphone/muted).
    /// MUST be a UnityEngine.Texture2D; a non-Texture2D renders nothing. Hidden unless <see cref="ShowVoiceIcon"/>.</summary>
    public object? VoiceIcon;
    /// <summary>Tint for <see cref="VoiceIcon"/> (e.g. green while talking). <c>default</c> (alpha 0) = white.</summary>
    public ColorRgba VoiceIconTint;
    /// <summary>When true the <see cref="VoiceIcon"/> cell is shown (user toggle).</summary>
    public bool ShowVoiceIcon;
    /// <summary>Optional click handler for the voice-status icon cell. When set (non-null) the 14×14 voice
    /// cell becomes clickable — the framework enables the cell's raycast target and routes a click here
    /// (e.g. the local player clicking their own row's voice icon to cycle Speak → Listen → Mute). When
    /// null (the default) the cell is display-only and renders byte-identical to a plugin built before this
    /// field existed. Only fires while <see cref="ShowVoiceIcon"/> shows the cell.</summary>
    public Action? OnVoiceIconClick;
    /// <summary>Optional click handler for a debuff cell in the trailing 2×2 block. When set, each cell becomes
    /// clickable and the framework routes a left-click here with (<see cref="Id"/>, cellIndex 0..3) — the plugin
    /// maps the index to the debuff (and treats the overflow "+N" cell as "show all"). Null (the default) leaves
    /// the cells display-only, byte-identical to a plugin built before this field existed. Wired once at build,
    /// so a stable (cached) delegate here allocates nothing per refresh.</summary>
    public Action<EntityId, int>? OnDebuffClick;
    /// <summary>Optional colored box border around the whole row (e.g. green while a member is talking).
    /// <c>default</c> (alpha 0) = no border.</summary>
    public ColorRgba RowBorder;
    /// <summary>Battle-Imagine icon size.</summary>
    public ImagineSize ImagineSize;
    /// <summary>Battle-Imagine cluster placement.</summary>
    public ImaginePosition ImaginePosition;
    /// <summary>How the two values drawn ON the metric bar (per-second left, total right) are kept readable over the
    /// bar fill — relevant when the fill is a bright class colour. <c>default</c> = <see cref="MeterLabelStyle.Plain"/>
    /// (the pre-2.8 look), so plugins built before this field existed render unchanged.</summary>
    public MeterLabelStyle LabelStyle;
}

/// <summary>Treatment of the value labels drawn on a meter row's bar (<see cref="MeterRowData.LabelStyle"/>).
/// A player-facing choice (owner 2026-09-09: "it should be option for players: normal text, outline, smooth shadow").</summary>
public enum MeterLabelStyle
{
    /// <summary>Plain white text straight on the fill — the pre-2.8 look.</summary>
    Plain = 0,
    /// <summary>The framework's readability outline (the stat HUD's dark 1.1 px halo around every glyph).</summary>
    Outline = 1,
    /// <summary>A soft dark shade under each value, its strength following the fill's brightness; the glyphs stay untouched.</summary>
    Shadow = 2,
}
