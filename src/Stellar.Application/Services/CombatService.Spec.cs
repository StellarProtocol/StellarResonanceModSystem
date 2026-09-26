using System;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Application.Services;

/// <summary>
/// <c>ICombatSpec</c> — buff-first spec resolution (spec-from-talent-buffs, 2026-09-26). The resolution rules
/// live in <see cref="TalentSpecResolver"/>; this partial feeds it (cast spec from damage events) and turns its
/// real value changes into <see cref="CombatEvent.SpecChanged"/> on the drain.
/// </summary>
internal sealed partial class CombatService
{
    public int GetSubProfession(EntityId entityId) => _spec.GetSubProfession(entityId);

    public bool TryGetTalentSpec(EntityId entityId, out int subProfessionId)
        => _spec.TryGetTalentSpec(entityId, out subProfessionId);

    // Cast FALLBACK (last-seen-wins): recognise the caster's spec from the CAST skill id — the ZDPS-parity
    // method, used for entities with no talent root buff. Only player sources can have a spec;
    // SubProfessionFromSkill already returns null for non-spec skills, so the IsPlayer gate just avoids work.
    // A talent-derived spec is NOT overwritten: the resolver keeps the two sources apart.
    private void AccumulateSpec(CombatEvent evt)
    {
        if (evt is not CombatEvent.DamageDealt d) return;
        if (d.SourceId.IsNone || !d.SourceId.IsPlayer) return;
        if (ProfessionSpecs.SubProfessionFromSkill(d.SkillId) is { } sub && _entities.SetSubProfession(d.SourceId, sub))
            _spec.MarkDirty(d.SourceId, d.TimestampMs);
    }

    // Packet bracket. The network thread holds _packetLock for the whole ingest of one WorldNtf packet
    // (re-entrant: Monitor), _ingestDepth counts nested brackets (only touched under the lock). The main-thread
    // publish TryEnters the same lock and never blocks: a busy frame is skipped and the dirty set waits.
    private readonly object _packetLock = new();
    private int _ingestDepth;

    /// <summary>Test seam: the wall clock the talent-spec gap timer reads.</summary>
    internal Func<long> SpecClock { set => _spec.Clock = value; }

    /// <summary>The wire probe brackets each packet's ingest (paired with <see cref="EndPacket"/> in
    /// try/finally). Spec changes are never published while a bracket is open on ANY thread, so a packet carrying
    /// attr 220 AND the new root (a class swap) yields exactly one SpecChanged; <see cref="ResetEntities"/> takes
    /// the same lock, so a scene reset cannot interleave with a publish either.</summary>
    public void BeginPacket()
    {
        Monitor.Enter(_packetLock);
        _ingestDepth++;
    }

    /// <inheritdoc cref="BeginPacket"/>
    public void EndPacket()
    {
        _ingestDepth--;
        Monitor.Exit(_packetLock);
    }

    // Re-resolve entities whose inputs changed since the last drain and publish only REAL value changes.
    // Skipped while a packet is mid-ingest (the dirty set simply waits for the next drain); the lock is held
    // across TakeDirty → Publish → EnqueueEvent so neither a packet nor a reset can land in between. Returns true when
    // events were enqueued (the caller drains them in the same frame).
    private bool PublishSpecChanges()
    {
        if (!Monitor.TryEnter(_packetLock)) return false;   // a packet is mid-ingest on another thread
        try
        {
            if (_ingestDepth > 0) return false;             // … or on this thread (nested call)
            var changes = _spec.Publish(_spec.TakeDirty());
            if (changes is null) return false;
            for (var i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                DiagSpecChange(c);
                EnqueueEvent(c);
            }
            return true;
        }
        finally
        {
            Monitor.Exit(_packetLock);
        }
    }
}
