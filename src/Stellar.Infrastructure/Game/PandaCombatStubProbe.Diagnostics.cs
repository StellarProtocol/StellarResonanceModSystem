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

    // One-shot ENTER-SCENE STRUCTURE diagnostic. Fires once per scene-enter that
    // carries a SceneAttrs sub-message, REGARDLESS of STELLAR_DIAGNOSTICS (same
    // policy as the other boot one-shots — the toggle gates per-event repetition,
    // not structure discovery). Surfaces the offline-unconfirmable layout so the
    // user's live run can pin which field is the stable per-run id:
    //   - top-level EnterScene fields (field# + wire type)
    //   - EnterSceneInfo.SceneGuid (field 3, string)
    //   - every EnterSceneInfo.SceneAttrs row: attr id + int64-decoded value
    //     (AttrSceneUuid=342 is the candidate run id; AttrSceneBasicId=341 is the
    //     dungeon TEMPLATE — same across runs; compare across runs to confirm)
    //   - a hex dump of the first 64 payload bytes
    // To CONFIRM: the AttrSceneUuid value must be IDENTICAL across every
    // enter-scene log within one dungeon run and DIFFER between separate runs.
    private int _enterSceneStructLogCount;
    private const int EnterSceneStructLogMax = 30;
    private const int EnterSceneHexDumpBytes = 64;

    private void DiagEnterSceneStructure(ReadOnlySpan<byte> payload)
    {
        // Log EVERY enter-scene (capped) so a live run shows the town->dungeon
        // sequence of AttrSceneUuid values — needed to confirm whether attr 342 is
        // per-instance (changes per dungeon entry) or a persistent/home scene id.
        if (_enterSceneStructLogCount >= EnterSceneStructLogMax) return;
        _enterSceneStructLogCount++;

        _log.Info($"[EnterScene][struct] top-level fields: {DescribeTopLevelFields(payload)} " +
                  $"(len={payload.Length})");

        if (EnterSceneReader.TryReadSceneAttrs(payload, out var sceneAttrs, out var sceneGuid))
        {
            _log.Info($"[EnterScene][struct] SceneGuid=\"{sceneGuid ?? "<none>"}\" " +
                      $"SceneAttrs.uuid={sceneAttrs.Uuid} attrCount={sceneAttrs.Items.Count}");
            for (int i = 0; i < sceneAttrs.Items.Count; i++)
            {
                var attr = sceneAttrs.Items[i];
                string note = attr.Id switch
                {
                    AttrTypeIds.AttrSceneUuid    => " <-- AttrSceneUuid (candidate STABLE run id)",
                    AttrTypeIds.AttrSceneBasicId => " (AttrSceneBasicId = dungeon TEMPLATE, not a run id)",
                    AttrTypeIds.AttrSceneName    => " (AttrSceneName)",
                    AttrTypeIds.AttrSceneLevelId => " (AttrSceneLevelId)",
                    _ => string.Empty,
                };
                _log.Info($"[EnterScene][struct]   sceneAttr id={attr.Id} " +
                          $"long={attr.DecodedLong} bytes={attr.RawData.Length}{note}");
            }
        }
        else
        {
            _log.Info("[EnterScene][struct] SceneAttrs sub-message absent or malformed " +
                      "(no per-run scene id available from this enter-scene)");
        }

        _log.Info($"[EnterScene][struct] hex[0..{EnterSceneHexDumpBytes}]={HexDump(payload, EnterSceneHexDumpBytes)}");
    }

    // Walk the top-level protobuf fields, listing each (field#, wireType) tag
    // without descending. Defensive — stops at the first malformed tag.
    private static string DescribeTopLevelFields(ReadOnlySpan<byte> payload)
    {
        var sb = new System.Text.StringBuilder();
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"f{field}/w{wire}");
            if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        return sb.Length == 0 ? "<none>" : sb.ToString();
    }

    private static string HexDump(ReadOnlySpan<byte> payload, int max)
    {
        int n = payload.Length < max ? payload.Length : max;
        var sb = new System.Text.StringBuilder(n * 2);
        for (int i = 0; i < n; i++) sb.Append(payload[i].ToString("x2"));
        return sb.ToString();
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

    // Boss-recon spike (Task 1): build the (id, val) attr list from an appearing
    // entity and delegate to MonsterCatalogService.DiagMonster. Always-on — not
    // gated on StellarDiagnostics (same policy as DiagEnterSceneStructure).
    private void DiagBossRecon(AppearEntityMsg entity)
    {
        if (entity.Attrs is not { } attrs)
        {
            _monsterCatalog.DiagMonster(entity.Uuid, System.Array.Empty<(int, long)>());
            return;
        }

        var list = new System.Collections.Generic.List<(int id, long val)>(attrs.Items.Count);
        for (int i = 0; i < attrs.Items.Count; i++)
        {
            var a = attrs.Items[i];
            list.Add((a.Id, a.DecodedLong));
        }

        _monsterCatalog.DiagMonster(entity.Uuid, list);
    }
}
