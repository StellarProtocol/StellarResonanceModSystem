using System;
using System.Reflection;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Blackout-rescue concern of <see cref="PandaPlayerStateProbe"/>.
///
/// <para><b>The trigger is mounting, not relaunching.</b> Owner toggle
/// 2026-07-29: mount => the meter's self row reads hp 0, dismount => hp returns,
/// mount again => 0 again. So <c>ZEntityMgr</c>'s player entity stops yielding
/// attributes for as long as the player is mounted; the earlier
/// "relaunch while mounted" repro was just starting inside that state.</para>
///
/// <para><c>ZEntityMgr</c> keeps the player's uuid and the player's entity in
/// TWO separate fields (<c>playerUuid_</c> / <c>playerEnt_</c>), and all four
/// entity accessors return the latter — so if only <c>playerEnt_</c> is swapped
/// or cleared while mounted, the player is still reachable by uuid. Two
/// independent re-lookups are therefore tried, in order:
/// <list type="number">
///   <item><c>GetEntity(long uuid)</c> with the manager's own player uuid.</item>
///   <item><c>GetCharEntity(long charId)</c> with the char id from the char
///         record (authoritative — <c>CharSerialize.CharId</c>).</item>
/// </list></para>
///
/// <para>A replacement is accepted ONLY if it validates as the local player
/// (<c>CharId</c> match, else <c>IsPlayerCtrl</c>/<c>IsPlayer</c>). Never accept
/// an unvalidated entity: reading the mount's vitals or position would corrupt
/// the CombatMeter replay, which is a hard product requirement.</para>
///
/// <para>Also emits a BOUNDED usefulness-transition log. The pre-existing failure
/// log is one-shot, which is why a 2-second boot gap and a multi-minute mounted
/// blackout produced byte-identical logs and the bug stayed undiagnosable. This
/// logs each flip (capped) with the discriminating facts, so a single mount
/// toggle reveals the mechanism even when both rescues fail.</para>
/// </summary>
internal sealed partial class PandaPlayerStateProbe
{
    private MethodInfo? _mgrGetEntityByUuid;   // ZEntityMgr.GetEntity(long uuid)
    private MethodInfo? _mgrGetCharEntity;     // ZEntityMgr.GetCharEntity(long charId)
    private PropertyInfo? _mgrPlayerUuid;      // ZEntityMgr.MainEntUuid / PlayerUuid
    private bool _rescueResolved;

    // Validation members, resolved off the live entity's runtime type (ZEntity is
    // abstract — the real object is PlayerEnt / CharEnt / …).
    private PropertyInfo? _entCharId;
    private PropertyInfo? _entUuid;
    private PropertyInfo? _entIsPlayer;
    private PropertyInfo? _entIsPlayerCtrl;
    private Type? _entMembersForType;

    private readonly object?[] _rescueArgs = new object?[1];

    // Transition log state.
    private bool? _lastUseful;
    private int _transitionsLogged;
    private const int MaxTransitionsLogged = 12;

    /// <summary>
    /// The entity THIS tick's <see cref="TrySample"/> read successfully — the raw
    /// <c>playerEnt_</c> when healthy, or the validated rescue when it went dark.
    /// Handed to sibling probes via <c>GetLocalPlayerEntity</c>.
    ///
    /// <para><b>Same-tick only, by design.</b> It is cleared at the top of every
    /// <see cref="TrySample"/> and set only on success, so it never survives into
    /// a later tick. Holding an IL2CPP entity reference ACROSS ticks would risk
    /// handing out an object the game has since destroyed — an uncatchable
    /// native access violation, not a catchable exception (see
    /// <c>docs/il2cpp-probing-safety.md</c>). The handoff is safe because Host
    /// refreshes player-state and player-stats back-to-back in one synchronous
    /// tick (<c>Wiring.ServiceTick.cs</c>), so no scene teardown can intervene.</para>
    /// </summary>
    private object? _tickGoodEntity;

    /// <summary>
    /// Attempts to produce a usable snapshot from a re-looked-up local-player
    /// entity. Returns false when neither route yields a validated entity with
    /// readable attributes.
    /// </summary>
    private bool TryRescueSample(object mgr, out PlayerStateSnapshot snapshot, out string via)
    {
        snapshot = default;
        via = "none";
        EnsureRescueResolved();

        var expectedCharId = LocalCharIdOrZero();

        if (TryRoute(mgr, _mgrGetEntityByUuid, ReadPlayerUuid(mgr), expectedCharId, ref snapshot))
        {
            via = "GetEntity(uuid)";
            return true;
        }
        if (TryRoute(mgr, _mgrGetCharEntity, expectedCharId, expectedCharId, ref snapshot))
        {
            via = "GetCharEntity(charId)";
            return true;
        }
        return false;
    }

    // Invokes one single-long-arg lookup, validates the result is the local
    // player, and captures from it. False on any miss.
    private bool TryRoute(object mgr, MethodInfo? lookup, long key, long expectedCharId, ref PlayerStateSnapshot snapshot)
    {
        if (lookup is null || key == 0)
        {
            return false;
        }

        object? found;
        try
        {
            _rescueArgs[0] = key;
            found = lookup.Invoke(mgr, _rescueArgs);
        }
        catch
        {
            return false;
        }
        if (found is null || !IsLocalPlayerEntity(found, expectedCharId))
        {
            return false;
        }

        var candidate = CaptureSnapshot(found);
        if (!IsUseful(candidate))
        {
            return false;
        }
        snapshot = candidate;
        _tickGoodEntity = found;   // same-tick handoff for sibling probes
        return true;
    }

    private bool IsLocalPlayerEntity(object entity, long expectedCharId)
    {
        EnsureEntityMembers(entity.GetType());
        return LocalPlayerEntityCheck.Validates(
            entityCharId: ReadLong(_entCharId, entity),
            expectedCharId: expectedCharId,
            isPlayerCtrl: ReadBool(_entIsPlayerCtrl, entity),
            isPlayer: ReadBool(_entIsPlayer, entity));
    }

    private long LocalCharIdOrZero()
    {
        if (_charIdentityReader is null) return 0L;
        return _charIdentityReader.TryRead(out var identity) ? identity.CharId : 0L;
    }

    private long ReadPlayerUuid(object mgr) => ReadLong(_mgrPlayerUuid, mgr);

    private void EnsureRescueResolved()
    {
        if (_rescueResolved) return;
        _rescueResolved = true;
        if (_zEntityMgrType is null) return;

        _mgrGetEntityByUuid = FindSingleLongMethod(_zEntityMgrType, "GetEntity");
        _mgrGetCharEntity = FindSingleLongMethod(_zEntityMgrType, "GetCharEntity");
        _mgrPlayerUuid = _zEntityMgrType.GetProperty("MainEntUuid", AnyInstance)
            ?? _zEntityMgrType.GetProperty("PlayerUuid", AnyInstance);
    }

    // ZEntityMgr overloads GetEntity; we want the single long-keyed uuid lookup.
    private static MethodInfo? FindSingleLongMethod(Type owner, string name)
    {
        foreach (var m in owner.GetMethods(AnyInstance))
        {
            if (m.Name != name || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;
            var t = ps[0].ParameterType;
            if (t == typeof(long) || t == typeof(int)) return m;
        }
        return null;
    }

    private void EnsureEntityMembers(Type entityType)
    {
        if (_entMembersForType == entityType) return;
        _entMembersForType = entityType;
        _entCharId = entityType.GetProperty("CharId", AnyInstance);
        _entUuid = entityType.GetProperty("Uuid", AnyInstance);
        _entIsPlayer = entityType.GetProperty("IsPlayer", AnyInstance);
        _entIsPlayerCtrl = entityType.GetProperty("IsPlayerCtrl", AnyInstance);
    }

    private static long ReadLong(PropertyInfo? property, object target)
    {
        if (property is null) return 0L;
        try
        {
            return property.GetValue(target) switch
            {
                long l => l,
                int i => i,
                ulong ul => unchecked((long)ul),
                uint u => u,
                _ => 0L,
            };
        }
        catch { return 0L; }
    }

    private static bool ReadBool(PropertyInfo? property, object target)
    {
        if (property is null) return false;
        try { return property.GetValue(target) is true; }
        catch { return false; }
    }

    /// <summary>
    /// Logs each flip of entity usefulness, capped at
    /// <see cref="MaxTransitionsLogged"/> lines per session. Deliberately NOT
    /// gated on diagnostics: the owner's client runs diagnostics=OFF, and without
    /// this a recurrence is indistinguishable from a boot transient.
    /// </summary>
    private void NoteUsefulness(object mgr, object entity, bool useful, string via)
    {
        if (_lastUseful == useful) return;
        _lastUseful = useful;
        if (_transitionsLogged >= MaxTransitionsLogged) return;
        _transitionsLogged++;

        EnsureRescueResolved();
        EnsureEntityMembers(entity.GetType());
        _log.Info($"[PlayerState] entity {(useful ? "READABLE" : "BLACKOUT")} via={via} " +
                  $"type={entity.GetType().Name} entUuid={ReadLong(_entUuid, entity)} " +
                  $"entCharId={ReadLong(_entCharId, entity)} isPlayer={ReadBool(_entIsPlayer, entity)} " +
                  $"isPlayerCtrl={ReadBool(_entIsPlayerCtrl, entity)} mgrPlayerUuid={ReadPlayerUuid(mgr)} " +
                  $"recordCharId={LocalCharIdOrZero()}");
    }
}

/// <summary>
/// The safety gate on blackout rescue: decides whether a re-looked-up entity may
/// be treated as the local player. Pure so it can be pinned by tests —
/// accepting the wrong entity would feed the mount's (or another character's)
/// vitals and POSITION into <c>IPlayerState</c>, and position corruption breaks
/// the CombatMeter replay, which is a hard product requirement.
/// </summary>
internal static class LocalPlayerEntityCheck
{
    /// <summary>
    /// True only when the entity can be affirmatively tied to the local player.
    /// A matching char id is conclusive. Two KNOWN but different char ids are a
    /// conclusive rejection — the game's player flags must not override that.
    /// With no char id to compare, fall back to the game's own player-control
    /// flags; if those are false too, reject (unknown means no).
    /// </summary>
    internal static bool Validates(long entityCharId, long expectedCharId, bool isPlayerCtrl, bool isPlayer)
    {
        if (expectedCharId != 0 && entityCharId != 0)
        {
            return entityCharId == expectedCharId;
        }
        return isPlayerCtrl || isPlayer;
    }
}
