using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// The CombatMeter row elements, split out of WindowElement.cs (500-LoC file gate) when MeterRowElement grew its
// opt-in rounded style. Pure relocation of the two records; their shapes are unchanged.

/// <summary>Wraps a row <paramref name="Child"/> with a per-row accent backdrop drawn BEHIND it: a faint
/// <paramref name="Stripe"/>-tinted bar whose width is <paramref name="Share"/> (0..1) of the row, plus a 3-px
/// <paramref name="Stripe"/>-coloured left edge. The CombatMeter History/Skill table rows use this for the
/// role-coloured stripe + share-fraction wash the IMGUI build drew (DrawRowAccent). Both Funcs are poll-diffed.</summary>
public sealed record AccentRowElement(HudElement Child, Func<ColorRgba> Stripe, Func<float> Share) : HudElement;

/// <summary>One bespoke CombatMeter row — the borderless, role-coloured, animated meter row (HP spine + class
/// crest + name·spec·share line + role-coloured metric bar with a per-second/total overlay + self highlight +
/// offline scrim). The framework reproduces the custom visual that the IMGUI <c>MeterRowView</c> drew, so the
/// meter keeps its distinct look (it does NOT ride the generic Row/Bar primitives). <paramref name="Data"/> is
/// re-pulled on the window's capped refresh (poll-diffed) — the plugin snapshots its per-combatant state so the
/// Func never allocates. Fixed 48-px row height; width fills the row. Compose inside a <see cref="ListElement"/>.</summary>
/// <param name="Data">Per-combatant row snapshot, re-pulled on the window's capped refresh.</param>
/// <param name="OnRightClick">Optional: invoked when the row is right-clicked (CombatMeter row context menu); null = no menu.</param>
public sealed record MeterRowElement(System.Func<MeterRowData> Data, System.Action? OnRightClick = null) : HudElement;
