using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Payload handlers for <see cref="PandaCombatStubProbe"/>. Each handler is
/// called from <c>Dispatch</c> after the postfix has confirmed the
/// <c>(uuid, methodId)</c> tuple is one we wire. Handlers parse a single
/// WorldNtf method body and forward decoded events to
/// <see cref="ICombatEventSink"/>.
///
/// <para>
/// Runs on the network receive thread; throwing from here propagates back
/// up through the dispatcher's try/catch and logs a single
/// warning. Individual readers (e.g. <see cref="AoiSyncDeltaReader"/>) are
/// expected to be allocation-light on the hot path.
/// </para>
/// </summary>
internal sealed partial class PandaCombatStubProbe
{
    private void OnServerTime(ReadOnlySpan<byte> span)
    {
        // SyncServerTime payload is a single int64 epoch_ms at field 1.
        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire)) break;
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(span, ref pos, out var t)) break;
                _sink.SetServerNowMs((long)t);
                return;
            }
            if (!WireProtocol.SkipField(span, ref pos, wire)) break;
        }
    }

    private void OnNearEntities(ReadOnlySpan<byte> span)
    {
        if (!SyncNearEntitiesReader.TryReadAppearAndDisappear(span, out var appears, out var disappears))
            return;

        // Disappears — drop combat-cache rows for entities that left AOI.
        foreach (var d in disappears)
            _sink.OnEntityDisappeared(new EntityId(d));

        // Appears — extract AttrName + the skill loadout (AttrSkillLevelIdList) from each entity's
        // AttrCollection. The full attr set (incl. the equipped-skill list that carries Battle Imagines)
        // ships on appear, NOT in subsequent deltas — so it must be read here, not only in ApplyAttrDeltas.
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var entity in appears)
        {
            DiagAppearEntity(entity);
            ReadAppearEntity(new EntityId(entity.Uuid), entity.Attrs, ts);
        }
    }

    // Per-entity attr fan-out for a SyncNearEntities APPEAR. Extracted from OnNearEntities to
    // keep both bodies under the 50-LoC gate. NEW vs the old inline walk: AttrHp/AttrMaxHp are
    // now read explicitly and seeded into the vitals cache — appear packets carry the full attr
    // set, so without this the FIRST DELTA after appear defined vitals, and a MaxHp-only delta
    // minted the false-dead {Hp:0, MaxHp:>0} state. Mirrors ApplyAttrDeltasForEntity's handling
    // (which also does NOT store hp/maxHp in the generic attr map).
    private void ReadAppearEntity(EntityId eid, AttrCollectionMsg? attrsOpt, long ts)
    {
        if (attrsOpt is not { } attrs) return;
        long summonerId = 0, topSummonerId = 0;
        long hp = -1, maxHp = -1;
        for (int i = 0; i < attrs.Items.Count; i++)
        {
            var attr = attrs.Items[i];
            if (attr.Id == AttrTypeIds.AttrName)
            {
                var name = attr.DecodedString;
                if (!string.IsNullOrEmpty(name)) _sink.UpdateEntityName(eid, name!);
            }
            else if (attr.Id == AttrTypeIds.AttrHp)    { hp = attr.DecodedLong; }
            else if (attr.Id == AttrTypeIds.AttrMaxHp) { maxHp = attr.DecodedLong; }
            else if (attr.Id == AttrTypeIds.AttrFightPoint)
            {
                _sink.UpdateEntityFightPoint(eid, attr.DecodedLong);
                _sink.SetEntityAttribute(eid, attr.Id, attr.DecodedLong);
            }
            else if (attr.Id == AttrTypeIds.AttrSkillLevelIdList)
            {
                var skills = SkillLevelListReader.Read(attr.RawData.Span);
                if (skills.Count > 0) _sink.UpdateEntitySkillLevels(eid, skills);
            }
            else
            {
                if (attr.Id == AttrTypeIds.AttrSummonerId) summonerId = attr.DecodedLong;
                else if (attr.Id == AttrTypeIds.AttrTopSummonerId) topSummonerId = attr.DecodedLong;
                CaptureEntityDetail(eid, attr, "appear");
            }
        }
        if (hp >= 0 || maxHp >= 0)
        {
            _sink.UpdateEntityVitals(eid, hp, maxHp);
            DiagAppearVitalsSeed(eid, hp, maxHp);
        }
        EmitSummonAppeared(eid, summonerId, topSummonerId, ts);
    }

    // Raises CombatEvent.EntitySummonAppeared when this appear carried a resolvable owner attribution
    // (AttrTopSummonerId preferred; AttrSummonerId as fallback). Most appearing entities (players,
    // unowned mobs) carry neither, so this is a no-op for the common case.
    private void EmitSummonAppeared(EntityId summonId, long summonerId, long topSummonerId, long timestampMs)
    {
        long owner = topSummonerId != 0 ? topSummonerId : summonerId;
        if (owner == 0) return;
        _sink.EnqueueEvent(new CombatEvent.EntitySummonAppeared(timestampMs, new EntityId(owner), summonId));
    }

    // EnterScene (method 3) — the local player's full Entity (PlayerEnt) carries self's COMPLETE attr set,
    // including the equipped-skill list (AttrSkillLevelIdList). Self is NOT in its own SyncNearEntities and
    // the skill list is absent from SyncToMeDelta deltas, so this is the only path yielding self's loadout.
    private void OnEnterScene(ReadOnlySpan<byte> span)
    {
        // Buffs are scene-scoped — the server drops them on a scene change without
        // sending per-buff remove events, so clear the accumulated set here (before
        // the self-attr parse / early-return) or stale debuffs (e.g. a lockout)
        // linger on the bar after a zone transition.
        _sink.ClearAllBuffs();

        // Entity caches are scene-scoped too. Dungeon mobs are frequently touched only via damage packets
        // (never a SyncNearEntities disappear), so their dps/hps/vitals/attr rows would otherwise survive
        // every dungeon re-entry and accumulate for the whole session — the FPS-decays-only-after-re-entry
        // leak. Wipe here, BEFORE the self re-population below; the new scene re-broadcasts self + AOI peers.
        _sink.ResetEntities();

        LatchDungeonRunId(span);
        DiagScanSceneAttrsForDeathCount(span);

        bool parsed = EnterSceneReader.TryReadPlayerEntity(span, out var self);
        if (!parsed || self.Attrs is not { } attrs) return;
        var eid = new EntityId(self.Uuid);
        for (int i = 0; i < attrs.Items.Count; i++)
        {
            var attr = attrs.Items[i];
            if (attr.Id == AttrTypeIds.AttrFightPoint)
            {
                _sink.UpdateEntityFightPoint(eid, attr.DecodedLong);
                _sink.SetEntityAttribute(eid, attr.Id, attr.DecodedLong);
            }
            else if (attr.Id == AttrTypeIds.AttrSkillLevelIdList)
            {
                var skills = SkillLevelListReader.Read(attr.RawData.Span);
                if (skills.Count > 0)
                {
                    _sink.UpdateEntitySkillLevels(eid, skills);
                }
            }
            else
            {
                CaptureEntityDetail(eid, attr, "enter-scene-self");
            }
        }
    }

    // Run id: the server-assigned per-instance scene uuid (AttrSceneUuid=342) rides on
    // EnterSceneInfo.SceneAttrs. It is the STABLE per-run id (shared by everyone in the
    // run, identical across the run). Every enter-scene fires here — instanced content
    // (dungeon / instanced world-boss / raid) AND town/open-world — so we route the uuid
    // through DungeonRunIdGate: an instanced snowflake becomes the run id; a town/field
    // scene resolves to 0. Setting 0 on a non-instanced scene is deliberate — it clears
    // the previous run's id so it CANNOT linger and get stamped onto a later open-world
    // run (the run-identity collision fix). The plugin latches _lastRunId at combat start
    // and reads LastSettlement at archive, so the dungeon->town archive window still
    // uploads correctly under the dungeon id even though CurrentRunId has dropped to 0.
    //
    // When the enter-scene carries no readable scene id (TryReadSceneId == false: absent
    // SceneAttrs or an explicit 0), we leave the current run id untouched rather than
    // clobbering a valid run from a malformed/partial packet.
    private void LatchDungeonRunId(ReadOnlySpan<byte> span)
    {
        if (EnterSceneReader.TryReadSceneId(span, out var sceneUuid))
            _dungeonSink.SetCurrentRun(DungeonRunIdGate.Resolve(sceneUuid));
    }

    private void OnNearDelta(ReadOnlySpan<byte> span)
    {
        if (!AoiSyncDeltaReader.TryReadList(span, out var deltas))
        {
            _log.Warning("[CombatStub] failed to parse SyncNearDeltaInfo");
            return;
        }
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // SyncNearDeltaInfo may carry:
        //   - the local entity (which is ALSO sent via SyncToMeDeltaInfo.BaseDelta
        //     — processing both produces phantom Applied/Removed pairs)
        //   - the same entity twice in one batch (server quirk under heavy load)
        // Filter both out before dispatch so each entity is diffed at most once
        // per delta batch.
        long selfUuid = _localEntityIdValue;
        if (selfUuid == 0L && deltas.Count <= 1)
        {
            // Hot path before SetLocalEntityId fires — just dispatch.
            ProcessDeltas(deltas, ts);
            return;
        }

        var seen = new HashSet<long>(deltas.Count);
        var filtered = new List<AoiSyncDeltaMsg>(deltas.Count);
        for (int i = 0; i < deltas.Count; i++)
        {
            var d = deltas[i];
            if (d.Uuid == selfUuid && selfUuid != 0L) continue;
            if (!seen.Add(d.Uuid)) continue;
            filtered.Add(d);
        }
        ProcessDeltas(filtered, ts);
    }

    private void OnSelfDelta(ReadOnlySpan<byte> span)
    {
        if (!AoiSyncToMeDeltaReader.TryReadOuter(span, out var msg))
        {
            _log.Warning("[CombatStub] failed to parse SyncToMeDeltaInfo");
            return;
        }
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (msg.Uuid != 0L)
        {
            _sink.SetLocalEntityId(new EntityId(msg.Uuid));
            // Cache locally so OnNearDelta can filter self-rows out of the
            // SyncNearDeltaInfo fanout. CombatService keeps its own copy (first
            // non-None wins) — this probe-local cache is for the fast filter
            // path without a virtual call back into the sink each delta.
            _localEntityIdValue = msg.Uuid;
        }

        // Cooldowns: replace snapshot wholesale.
        _sink.SetLocalCooldowns(msg.Cooldowns);

        if (msg.BaseDelta is { } baseDelta)
        {
            ProcessDeltas(new[] { baseDelta }, ts);
        }
    }

    // Orchestration over a single batch of AoiSyncDeltaMsg rows. Three
    // sequenced passes:
    //   1.  SkillUsed event fanout from the events list.
    //   1.5 Per-entity attribute fanout (AttrName / vitals / team id) via
    //       <see cref="ApplyAttrDeltas"/> — must run BEFORE damage so the
    //       label/team lookups in CombatMeter resolve synchronously.
    //   2.  Pre-attributed damage records via <see cref="ApplyDamageDeltas"/>.
    //   3.  Buff diffs.
    private void ProcessDeltas(IReadOnlyList<AoiSyncDeltaMsg> deltas, long timestampMs)
    {
        // Pass 1 — emit SkillUsed events. Pre-Batch-2 we also collected
        // SkillBegin into a dictionary so the HP-delta path could attribute
        // damage by "single caster present this tick"; that heuristic is gone
        // — SkillEffects.Damages carries AttackerUuid / TopSummonerId directly
        // — so this pass is now a pure fan-out.
        foreach (var d in deltas)
        {
            if (d.Events is null) continue;
            foreach (var ev in d.Events.Value.Events)
            {
                if (ev.EventType < 101 || ev.EventType > 105) continue;
                var phase = (SkillEventPhase)ev.EventType;
                int skillId = ev.IntParams.Count > 0 ? ev.IntParams[0] : 0;
                var casterId = new EntityId(d.Uuid);
                _sink.EnqueueEvent(new CombatEvent.SkillUsed(timestampMs, casterId, skillId, phase));
            }
        }

        // Pass 1.5 — per-entity attribute fan-out (AttrName / AttrHp / AttrMaxHp /
        // AttrTeamId). Runs BEFORE the damages loop so any value observed in
        // the same delta is queryable synchronously by downstream consumers.
        ApplyAttrDeltas(deltas);

        // Pass 2 — pre-attributed damage records from SkillEffects.Damages[].
        ApplyDamageDeltas(deltas, timestampMs);

        // Pass 3 — buff events (AoiSyncDelta field 10, BuffEffectSync).
        foreach (var d in deltas)
        {
            if (d.BuffEvents is not { } be || !be.Touched) continue;
            var entityId = new EntityId(d.Uuid);
            DiagBuffEvents(d.Uuid, be.Upserts, be.Removes);
            _sink.ApplyBuffEvents(entityId, be.Upserts, be.Removes, timestampMs);
        }
    }

    // Per-entity attribute fan-out. AttrName / AttrHp / AttrMaxHp / AttrTeamId
    // ride on AttrCollection together; surface each through ICombatLookup so
    // plugins (CombatMeter, etc.) can render readable labels, live HP bars,
    // and team-coloured rows next to damage rows.
    private void ApplyAttrDeltas(IReadOnlyList<AoiSyncDeltaMsg> deltas)
    {
        foreach (var d in deltas)
        {
            if (d.Attrs is { } attrCol)
                ApplyAttrDeltasForEntity(new EntityId(d.Uuid), attrCol);
        }
    }

    private void ApplyAttrDeltasForEntity(EntityId eid, AttrCollectionMsg attrCol)
    {
        long hp = -1, maxHp = -1;
        long? teamId = null;
        long? fightPoint = null;
        for (int i = 0; i < attrCol.Items.Count; i++)
        {
            var attr = attrCol.Items[i];
            if (attr.Id == AttrTypeIds.AttrName)
            {
                var name = attr.DecodedString;
                if (!string.IsNullOrEmpty(name))
                    _sink.UpdateEntityName(eid, name!);
            }
            else if (attr.Id == AttrTypeIds.AttrHp)
            {
                hp = attr.DecodedLong;
            }
            else if (attr.Id == AttrTypeIds.AttrMaxHp)
            {
                maxHp = attr.DecodedLong;
            }
            else if (attr.Id == AttrTypeIds.AttrTeamId)
            {
                teamId = attr.DecodedLong;
            }
            else if (attr.Id == AttrTypeIds.AttrFightPoint)
            {
                fightPoint = attr.DecodedLong;
            }
            else if (attr.Id == AttrTypeIds.AttrSkillLevelIdList)
            {
                var skills = SkillLevelListReader.Read(attr.RawData.Span);
                if (skills.Count > 0)
                {
                    _sink.UpdateEntitySkillLevels(eid, skills);
                }
            }
            else
            {
                CaptureEntityDetail(eid, attr, "delta");
            }
        }
        if (hp >= 0 || maxHp >= 0)     _sink.UpdateEntityVitals(eid, hp, maxHp);
        if (teamId is long t)          _sink.UpdateEntityTeamId(eid, t);
        if (fightPoint is long fp)
        {
            _sink.UpdateEntityFightPoint(eid, fp);
            _sink.SetEntityAttribute(eid, AttrTypeIds.AttrFightPoint, fp);
        }
    }

    // Inspector-detail capture shared by all three attr-iteration sites
    // (appear / EnterScene self / delta). Stores the per-entity equipment
    // loadout (AttrEquipData=200) and the scalar identity/stat attrs the entity
    // inspector reads (level / season level / profession). Only varint-decodable
    // scalar attrs are stored as numbers — string/packed attrs are never routed
    // here. FightPoint is stored by each caller alongside its UpdateEntityFightPoint.
    // <paramref name="path"/> identifies which of the three call sites reached here —
    // consumed only by the AttrDeathCount(348) recon diagnostic (Task 6).
    private void CaptureEntityDetail(EntityId eid, AttrMsg attr, string path)
    {
        if (attr.Id == AttrTypeIds.AttrDeathCount) DiagDeathCountAttr(eid, attr, path);
        if (attr.Id == AttrTypeIds.AttrEquipData)
        {
            _sink.SetEntityEquipment(eid, AttrEquipDataReader.Read(attr.RawData.Span));
            return;
        }
        if (attr.Id == AttrTypeIds.AttrFashionData)
        {
            var fashion = AttrFashionDataReader.Read(attr.RawData.Span);
            _sink.SetEntityFashion(eid, fashion);
            DiagFashionDecoded(eid, fashion);
            return;
        }
        // Retain every SCALAR attr for the inspector's Attributes tab (the full ZDPS-style dump, not just
        // level/profession). DecodedLong is a safe varint-try (returns 0 on a non-varint payload), so this is
        // correct for numeric attrs and harmless otherwise. Skip the known non-scalar attrs so they don't
        // store a garbage number: AttrName (string → UpdateEntityName), AttrPos (packed Vec3),
        // AttrSkillLevelIdList (repeated message → handled via the skills path / SetEntitySkillLevels).
        if (attr.Id == AttrTypeIds.AttrName
         || attr.Id == AttrTypeIds.AttrPos
         || attr.Id == AttrTypeIds.AttrSkillLevelIdList) return;
        // Skip zero: a non-varint (string/packed) payload decodes to 0, so this drops junk entries that would
        // otherwise pad every entity's attr map. Legit zero-valued scalar attrs (rare) are simply omitted from
        // the Attributes tab — acceptable for a raw debug dump.
        var value = attr.DecodedLong;
        if (value != 0) _sink.SetEntityAttribute(eid, attr.Id, value);
    }

    // Pre-attributed damage fan-out. The wire carries SyncDamageInfo with
    // attacker / skill / crit / element already populated; no need to infer
    // from HP deltas. AttrCollection is still parsed in ApplyAttrDeltas (HP
    // values may be consumed by other surfaces, e.g. IPlayerState) but does
    // not drive damage events.
    private void ApplyDamageDeltas(IReadOnlyList<AoiSyncDeltaMsg> deltas, long timestampMs)
    {
        foreach (var d in deltas)
        {
            if (d.Damages.Count == 0) continue;
            var targetId = new EntityId(d.Uuid);
            for (int i = 0; i < d.Damages.Count; i++)
            {
                var dmg = d.Damages[i];
                if (_damageLogCount < DamageLogCap)
                {
                    _damageLogCount++;
                    _log.Info(
                        $"[Combat] damage target={d.Uuid} skill={dmg.OwnerId} amount={dmg.HpLessenValue}|{dmg.Value}|{dmg.LuckyValue} " +
                        $"attacker={dmg.AttackerUuid} top={dmg.TopSummonerId} crit={(dmg.TypeFlag & 1) != 0} (#{_damageLogCount}/{DamageLogCap})");
                }
                else
                {
                    DiagDamage(d.Uuid, dmg);
                }
                _sink.IngestDamage(dmg, targetId, timestampMs);
            }
        }
    }
}
