using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.GameData;

/// <summary>
/// Shared cooldown math for Battle-Imagine (and any multi-charge) skills, used by both the CooldownBar and the
/// CombatMeter so their timing can't drift. Two parts:
/// <list type="bullet">
/// <item><see cref="EffectiveDuration"/> — the real per-charge recharge: the wire <c>duration</c> (or
/// <c>valid_cd_time</c>) reduced by cooldown reduction (every player has a flat ~10% default; gear/buffs add the
/// cooldown-acceleration attrs 11960 skill / 11980 imagine). Haste (atk/cast speed) does NOT affect this.</item>
/// <item><see cref="Update"/> — a sequential-recharge simulator. The wire moves <c>begin</c> only ON CAST and never
/// reports a live charge count, and after the first charge's window expires it says nothing about the remaining
/// charges still recharging. So we simulate: each cast (begin advances) spends one charge and re-anchors the
/// soonest-charge window; charges then return one per <c>perCharge</c>; time-to-full counts down continuously.</item>
/// </list>
/// One instance per consumer (it holds per-key recharge state). Unity-free and pure → unit-testable.
/// </summary>
public sealed class ImagineCooldownCalc
{
    /// <summary>Result of <see cref="Update"/> for one key this tick.</summary>
    /// <param name="Active">True while not fully charged (show a tile); false when full (hide).</param>
    /// <param name="ToFullMs">Milliseconds until ALL charges are back (counts down continuously across charges).</param>
    /// <param name="ChargesAvailable">Inferred charges currently usable (0..max).</param>
    /// <param name="FullFraction">Progress toward full, 0..1 (1 = full) — for a fill bar / radial sweep.</param>
    public readonly record struct Recharge(bool Active, int ToFullMs, int ChargesAvailable, float FullFraction);

    /// <summary>
    /// Effective per-charge recharge in ms: <c>(validCdMs &gt; 0 ? validCdMs : durMs) × (1 − cdReductionFraction)</c>.
    /// </summary>
    /// <param name="durMs">Wire <c>duration</c> (nominal per-charge recharge).</param>
    /// <param name="validCdMs">Wire <c>valid_cd_time</c> when populated (else 0 → falls back to <paramref name="durMs"/>).</param>
    /// <param name="cdReductionFraction">Total cooldown reduction, 0..1 (e.g. 0.10 default + attr/10000).</param>
    public static int EffectiveDuration(int durMs, int validCdMs, float cdReductionFraction)
    {
        int raw = validCdMs > 0 ? validCdMs : durMs;
        float r = cdReductionFraction < 0f ? 0f : cdReductionFraction > 0.95f ? 0.95f : cdReductionFraction;
        return (int)(raw * (1f - r));
    }

    private struct State { public long LastBegin; public int Spent; public long NextReadyAt; }
    private readonly Dictionary<int, State> _state = new();

    /// <summary>
    /// Advance the simulated recharge for <paramref name="key"/> (the base skill id) and return the current state.
    /// </summary>
    /// <param name="key">Stable per-skill key (the base imagine skill id).</param>
    /// <param name="beginMs">Wire <c>skill_begin_time</c> (changes only on cast).</param>
    /// <param name="perChargeMs">Per-charge recharge (from <see cref="EffectiveDuration"/>).</param>
    /// <param name="maxCharges">Max charges (SkillTable MaxEnergyChargeNum).</param>
    /// <param name="nowMs">Current server time (ms).</param>
    public Recharge Update(int key, long beginMs, int perChargeMs, int maxCharges, long nowMs)
    {
        if (maxCharges < 1) maxCharges = 1;
        if (perChargeMs < 1) perChargeMs = 1;
        if (!_state.TryGetValue(key, out var s)) s = new State { LastBegin = beginMs, Spent = 0, NextReadyAt = 0 };

        if (beginMs > s.LastBegin + 500)                       // new cast → one more charge out
        {
            bool wasIdle = s.Spent <= 0;
            s.Spent = s.Spent + 1 > maxCharges ? maxCharges : s.Spent + 1;
            // Only anchor the soonest-charge timer when nothing was recharging. If a charge is ALREADY recharging,
            // this new charge queues BEHIND it (sequential) — keep the running timer, don't restart it (else a 2nd
            // cast at 30s-left would wrongly reset to a full perCharge → 54+54 instead of 30+54).
            if (wasIdle) s.NextReadyAt = beginMs + perChargeMs;
            s.LastBegin = beginMs;
        }
        else if (s.Spent == 0 && beginMs + perChargeMs > nowMs)   // first sight mid-recharge → assume one out
        {
            s.Spent = 1;
            s.NextReadyAt = beginMs + perChargeMs;
            s.LastBegin = beginMs;
        }

        while (s.Spent > 0 && s.NextReadyAt > 0 && nowMs >= s.NextReadyAt)   // each completed perCharge returns one
        {
            s.Spent--;
            s.NextReadyAt += perChargeMs;
        }

        _state[key] = s;
        if (s.Spent <= 0) return new Recharge(false, 0, maxCharges, 1f);

        long toFull = (long)(s.Spent - 1) * perChargeMs + (s.NextReadyAt - nowMs);
        if (toFull < 0) toFull = 0;
        float frac = 1f - toFull / (float)System.Math.Max(1, (long)maxCharges * perChargeMs);
        frac = frac < 0f ? 0f : frac > 1f ? 1f : frac;
        return new Recharge(true, (int)toFull, maxCharges - s.Spent, frac);
    }
}
