using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;
using Stellar.Application.Abstractions;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaCombatStubProbe"/>. Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state stub-callback path pays
/// zero cost; flip it on to log every first-seen <c>(uuid, methodId)</c>
/// tuple plus an additional 95 damage-event lines per session.
///
/// Two entry points called from the main partial file:
/// <list type="bullet">
/// <item><see cref="DiagFirstStub"/> — first observation of each
/// <c>(uuid, methodId)</c> tuple this session, with the concrete IL2CPP
/// message type resolved for WorldNtf methods.</item>
/// <item><see cref="DiagDamage"/> — extends the production damage-fanout log
/// (capped at 5) to 100 events when diagnostics is on.</item>
/// </list>
/// </summary>
internal sealed partial class PandaCombatStubProbe
{
    // Diagnostic-only state. Live in the partial so the main file stays clean.

    // Track (uuid, methodId) pairs so each unique service+method combo is
    // logged once per session — same method id under a different service uuid
    // is still a new datum.
    private static readonly HashSet<(ulong Uuid, uint MethodId)> SeenCalls = new();
    private static readonly object SeenLock = new();

    // Additional damage-fanout cap that fires only when STELLAR_DIAGNOSTICS=1.
    // Production keeps the first 5 events as a sanity check; diagnostics mode
    // adds 95 more so we have a wider window for investigating combat-event
    // shape during a recon session.
    private int _diagDamageLogCount;
    private const int DiagDamageLogExtra = 95;

    /// <summary>
    /// Diagnostics mode: log the first observation of each
    /// <c>(uuid, methodId)</c> tuple. Captures every pair the stub layer
    /// observes, including foreign services and unwired WorldNtf methods —
    /// useful when identifying which method carries damage / skill-end
    /// without guessing field numbers from the .proto registry.
    /// </summary>
    private void DiagFirstStub(object stubCall, Type stubType, ulong uuid, uint methodId)
    {
        if (!StellarDiagnostics.IsEnabled) return;

        bool first;
        lock (SeenLock) first = SeenCalls.Add((uuid, methodId));
        if (!first) return;

        string msgTypeName = "?";
        if (uuid == BPSRServiceIds.WorldNtf)
        {
            try
            {
                var getCallMsg = stubType.GetMethod(
                    "GetCallMsg", BindingFlags.Instance | BindingFlags.Public);
                if (getCallMsg is not null)
                {
                    var m = getCallMsg.Invoke(stubCall, null);
                    // IStubCall.GetCallMsg returns the IL2CPP-projected
                    // interface (Google.Protobuf.IBufferMessage). The managed
                    // wrapper's .GetType() always projects to that interface
                    // name, which is useless for identifying which method
                    // carries damage / skill-end. Drill through to the IL2CPP
                    // runtime class via il2cpp_object_get_class to recover
                    // the concrete namespace + class name (e.g.
                    // "Zproto.AoiSyncDelta").
                    if (m is not null)
                    {
                        msgTypeName = ResolveIl2CppConcreteType(m)
                            ?? m.GetType().FullName
                            ?? "<unknown>";
                    }
                    else
                    {
                        msgTypeName = "<null>";
                    }
                }
            }
            catch (Exception mx)
            {
                msgTypeName = $"<threw {mx.GetType().Name}>";
            }
        }
        _log.Info($"[CombatStub] first OnCallStub uuid={uuid} method={methodId} msgType={msgTypeName}");
    }

    /// <summary>
    /// Diagnostics mode: extend the production damage-fanout log (capped at 5
    /// in the main file) by an additional 95 events per session, surfacing
    /// up to ~100 damage events for combat-shape investigations.
    /// </summary>
    private void DiagDamage(long targetUuid, SyncDamageInfoMsg dmg)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_diagDamageLogCount >= DiagDamageLogExtra) return;
        _diagDamageLogCount++;
        var total = DamageLogCap + _diagDamageLogCount;
        var cap = DamageLogCap + DiagDamageLogExtra;
        _log.Info(
            $"[Combat] damage target={targetUuid} skill={dmg.OwnerId} amount={dmg.HpLessenValue}|{dmg.Value}|{dmg.LuckyValue} " +
            $"attacker={dmg.AttackerUuid} top={dmg.TopSummonerId} crit={(dmg.TypeFlag & 1) != 0} (#{total}/{cap})");
    }

    // Names debug: dump the first N appear entities' attr-id set + AttrName value,
    // to see whether the wire actually ships AttrName(1) for non-self players and
    // mobs (CombatMeter falls back to Player#/Mob# when GetEntityName is empty).
    private int _entityAttrDumps;
    private const int EntityAttrDumpMax = 15;

    private void DiagAppearEntity(AppearEntityMsg entity)
    {
        if (!StellarDiagnostics.IsEnabled || _entityAttrDumps >= EntityAttrDumpMax) return;
        _entityAttrDumps++;
        var eid = new EntityId(entity.Uuid);
        var kind = eid.IsPlayer ? "player" : eid.IsMonster ? "mob" : "other";
        if (entity.Attrs is not { } attrs)
        {
            _log.Info($"[CombatStub][EntityName] appear uuid={entity.Uuid} kind={kind} uid={eid.Uid} attrs=NONE");
            return;
        }
        var ids = new int[attrs.Items.Count];
        string? nameVal = null;
        for (int i = 0; i < attrs.Items.Count; i++)
        {
            ids[i] = attrs.Items[i].Id;
            if (attrs.Items[i].Id == AttrTypeIds.AttrName) nameVal = attrs.Items[i].DecodedString;
        }
        _log.Info($"[CombatStub][EntityName] appear uuid={entity.Uuid} kind={kind} uid={eid.Uid} " +
            $"attrIds=[{string.Join(",", ids)}] AttrName={nameVal ?? "<none>"}");
    }

    // Appear-packet vitals seed (A1, 2026-07-17 sync spec). Doubles as the 3.7 attr-id validity
    // check the spec asks for: sane hp/maxHp values here confirm AttrHp=11310 / AttrMaxHp=11320
    // still hold (Stellar.Wire/AttrTypeIds.cs:26-27). Consumed by the Task 5 calibration session.
    private void DiagAppearVitalsSeed(EntityId eid, long hp, long maxHp)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[CombatStub][diag] appear vitals seed id={eid.Value} hp={hp} maxHp={maxHp}");
    }

    // One-shot boot diagnostic for the AttrFashionData(201) decode path. Fires
    // once per session regardless of STELLAR_DIAGNOSTICS (boot one-shots are
    // always on; the toggle only gates per-event repetition) — the single line
    // proves the wire path works and surfaces a live fashion_id so the Phase 3
    // gate can check whether cosmetics resolve through ItemTable.
    private bool _firstFashionDecodeLogged;

    private void DiagFashionDecoded(EntityId eid, IReadOnlyList<FashionEntry> entries)
    {
        if (_firstFashionDecodeLogged || entries.Count == 0) return;
        _firstFashionDecodeLogged = true;
        _log.Info($"[EntityDetail] first fashion decode: entity={eid.Value} entries={entries.Count} firstId={entries[0].FashionId}");
    }

    // Recon probe for Task 6 (Defeated / AttrDeathCount=348): the World-entity attr's
    // delivery path is NOT yet traced (unlike scene attrs 340-345, which are proven to
    // ride EnterSceneInfo.SceneAttrs). This logs every occurrence seen across the three
    // attr-iteration sites (appear / enter-scene self / delta) so the Task-9 in-game
    // smoke can confirm which carrier ships it and that its value matches the Victory
    // screen's "Defeated" count. Dedup'd per (entity, value) so a steady-state resend of
    // the same value doesn't spam the log; a genuinely changing count (0 -> 1 -> 2 ...)
    // still logs each new value.
    private static readonly HashSet<(long Uuid, long Value)> SeenDeathCountValues = new();
    private static readonly object SeenDeathCountLock = new();

    private void DiagDeathCountAttr(EntityId eid, AttrMsg attr, string path)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        long value = attr.DecodedLong;

        bool first;
        lock (SeenDeathCountLock) first = SeenDeathCountValues.Add((eid.Value, value));
        if (!first) return;

        _log.Info($"[CombatStub][DeathCount] AttrDeathCount(348) seen entity={eid.Value} value={value} path={path}");
    }

    // AttrDeathCount(348) is documented as a scene/World-level attr (same family as
    // AttrSceneName=340 / AttrSceneUuid=342 / AttrSceneLevelId=345), which ride
    // EnterSceneInfo.SceneAttrs — a DIFFERENT AttrCollection than PlayerEnt.Attrs (the
    // self entity's own attrs, scanned in OnEnterScene above). Scan SceneAttrs
    // separately on every EnterScene so we don't miss 348 if it never appears on any
    // per-entity AttrCollection at all. Uses EntityId(0) as the "no entity" sentinel
    // since scene-level attrs are not entity-scoped.
    private void DiagScanSceneAttrsForDeathCount(ReadOnlySpan<byte> span)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (!EnterSceneReader.TryReadSceneAttrs(span, out var sceneAttrs, out _)) return;
        for (int i = 0; i < sceneAttrs.Items.Count; i++)
        {
            var attr = sceneAttrs.Items[i];
            if (attr.Id == AttrTypeIds.AttrDeathCount)
                DiagDeathCountAttr(default, attr, "enter-scene-SceneAttrs");
        }
    }

    /// <summary>
    /// Recover the concrete IL2CPP class FullName for a boxed managed reference
    /// whose declared type is just an interface (e.g.
    /// <c>Google.Protobuf.IBufferMessage</c>). Reads
    /// <c>il2cpp_object_get_class -&gt; il2cpp_class_get_namespace/_name</c>
    /// via the native API. Returns null if the object isn't IL2CPP-backed or
    /// the lookup fails. Mirrors <c>PandaChatProbe.ResolveIl2CppConcreteType</c>
    /// — kept here as a private helper so the combat probe stays
    /// self-contained.
    /// </summary>
    private static string? ResolveIl2CppConcreteType(object boxedMsg)
    {
        try
        {
            if (boxedMsg is not Il2CppObjectBase obj)
            {
                return null;
            }
            var instancePtr = obj.Pointer;
            if (instancePtr == IntPtr.Zero) return null;

            var classPtr = IL2CPP.il2cpp_object_get_class(instancePtr);
            if (classPtr == IntPtr.Zero) return null;

            var nsPtr = IL2CPP.il2cpp_class_get_namespace(classPtr);
            var namePtr = IL2CPP.il2cpp_class_get_name(classPtr);
            var name = Marshal.PtrToStringAnsi(namePtr);
            if (string.IsNullOrEmpty(name)) return null;
            var ns = Marshal.PtrToStringAnsi(nsPtr);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }
        catch
        {
            return null;
        }
    }

}
