using System;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed partial class CombatService
{
    /// <summary>
    /// Ingest a pre-attributed <c>SyncDamageInfo</c> record sourced from
    /// <c>AoiSyncDelta.SkillEffects.Damages[]</c>. Picks the effective damage
    /// amount per visual-match precedence (<c>Value</c> &gt;
    /// <c>HpLessenValue</c> &gt; <c>LuckyValue</c>), prefers
    /// <c>TopSummonerId</c> over <c>AttackerUuid</c> for the source (so pet /
    /// totem damage attributes to the owner), and decodes the
    /// <c>TypeFlag</c> bits into IsCrit / IsLucky. Heal events are detected
    /// from <c>Type</c> and forwarded as DamageDealt with <c>IsHeal=true</c>
    /// — plugins filter as needed.
    /// </summary>
    public void IngestDamage(SyncDamageInfoMsg msg, EntityId targetId, long timestampMs)
    {
        // User visual check 2026-05-24: floating damage number (gross,
        // pre-mitigation "Value") was 1033 but HpLessenValue was 268; user
        // expects the meter to match the displayed number. Flip precedence to
        // Value > HpLessenValue > LuckyValue so totals/DPS aggregate the
        // gross attack value the player sees on-screen.
        var damage = msg.Value         > 0 ? msg.Value
                   : msg.HpLessenValue > 0 ? msg.HpLessenValue
                   :                         msg.LuckyValue;
        if (damage == 0) return;

        // One-shot field-semantics diagnostic — see _firstHitsLogged comment.
        if (Interlocked.Increment(ref _firstHitsLogged) <= DiagFirstHits)
        {
            _log.Info($"[Combat] first-hit dmg fields: Value={msg.Value} HpLessenValue={msg.HpLessenValue} LuckyValue={msg.LuckyValue} chosen={damage} type={msg.Type} flag={msg.TypeFlag}");
        }

        // TopSummonerId is the true caster for pets / totems; falls back to
        // the direct attacker when no summoner chain applies.
        var sourceUuid = msg.TopSummonerId != 0 ? msg.TopSummonerId : msg.AttackerUuid;
        var sourceId   = new EntityId(sourceUuid);

        // TypeFlag bit layout (verified against BPSR-Meter):
        //   bit 0 = crit, bit 2 = lucky-cause
        var isCrit  = (msg.TypeFlag & 0b001) != 0;
        var isLucky = (msg.TypeFlag & 0b100) != 0;

        // EDamageType.Heal = 2 (verified against
        // (local reference)).
        // Spec dispatch prompt said "value 1 → IsHeal" but the actual enum has
        // Normal=0, Miss=1, Heal=2 — Miss is its own value, not heal.
        var isHeal  = msg.Type == 2;

        EnqueueEvent(new CombatEvent.DamageDealt(
            timestampMs, sourceId, targetId,
            SkillId:        msg.OwnerId,
            Amount:         damage,
            ActualAmount:   msg.ActualValue,
            ShieldAbsorbed: msg.ShieldLessenValue,
            IsCrit:         isCrit,
            IsLucky:        isLucky,
            IsHeal:         isHeal,
            IsDead:         msg.IsDead,
            Element:        (DamageElement)msg.Property,
            SourceKind:     (DamageSourceKind)msg.DamageSource));
    }

    // --- Main thread drain ---

    public void Drain()
    {
        while (_queue.TryDequeue(out var evt))
        {
            lock (_ringLock)
            {
                if (_ring.Count >= RingCapacity) _ring.Dequeue();
                _ring.Enqueue(evt);
                Interlocked.Increment(ref _ringVersion);
            }
            AccumulateDps(evt);
            AccumulateHps(evt);
            AccumulateSpec(evt);
            FireEvent(evt);
        }

        // No idle decay: DpsAccumulator.Live is only updated on RecordDamage.
        // When no further events arrive for a source, its Live value freezes at
        // the most recent window-sum / 5s. UI consumers see the last observed
        // value until the next damage/heal event for that source. This matches
        // the user-visible expectation that the meter doesn't drift to 0 just
        // because the source briefly stopped attacking/healing.
    }

    private void AccumulateDps(CombatEvent evt)
    {
        if (evt is not CombatEvent.DamageDealt d) return;
        if (d.IsHeal) return;
        // Use Amount (gross Value per IngestDamage precedence) so the total
        // matches the floating damage number the player sees on-screen.
        // ActualAmount is the raw computed value before HP-clamping, which
        // can over-count vs. observed HP loss when the target dies mid-hit.
        if (d.Amount <= 0) return;
        if (d.SourceId.IsNone) return;
        _entities.AccumulateDps(d.SourceId, d.TimestampMs, d.Amount);
    }

    private void AccumulateHps(CombatEvent evt)
    {
        if (evt is not CombatEvent.DamageDealt d) return;
        if (!d.IsHeal) return;
        if (d.Amount <= 0) return;
        if (d.SourceId.IsNone) return;
        _entities.AccumulateHps(d.SourceId, d.TimestampMs, d.Amount);
    }

    // Resolve the caster's active spec from the CAST skill id (last-seen-wins). There is no authoritative
    // spec field on the wire and the equipped-skill loadout carries both specs' signature skills, so casts
    // are the only reliable signal (ZDPS-parity). Only player sources can have a spec; SubProfessionFromSkill
    // already returns null for non-spec / non-player skills, so the IsPlayer gate just avoids spurious work.
    private void AccumulateSpec(CombatEvent evt)
    {
        if (evt is not CombatEvent.DamageDealt d) return;
        if (d.SourceId.IsNone || !d.SourceId.IsPlayer) return;
        if (Stellar.Abstractions.Domain.GameData.ProfessionSpecs.SubProfessionFromSkill(d.SkillId) is { } sub)
            _entities.SetSubProfession(d.SourceId, sub);
    }

    private void FireEvent(CombatEvent evt)
    {
        var snapshot = _handlers;
        if (snapshot is null) return;
        for (var i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i](evt); }
            catch (Exception ex)
            {
                _log.Warning($"[Combat] subscriber threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
