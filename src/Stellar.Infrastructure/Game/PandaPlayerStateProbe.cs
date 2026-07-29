using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based player state probe that walks the Panda hot-update graph:
///   <c>ZUtil.ZSingleton&lt;Panda.ZGame.ZEntityMgr&gt;.Instance</c>
///     -> <c>MainEntity</c> / <c>PlayerEntity</c> (Panda.ZGame.ZEntity)
///       -> name / level / hp / mp / pos via the entity's <c>GetAttr</c>/<c>TryGetAttr</c>
///          overloads keyed by <c>Zproto.EAttrType</c>
///
/// First-success logs the discovered shape so iteration can converge fast.
/// </summary>
internal sealed partial class PandaPlayerStateProbe : IPlayerStateProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Char-record identity source. Optional: null in builds where Host wires no
    // inventory probe, in which case identity stays unavailable and behaviour is
    // unchanged. See PandaPlayerStateProbe.Identity.cs.
    private readonly PandaCharIdentityReader? _charIdentityReader;

    // Lazily-resolved cached reflection handles.
    private Type? _zEntityMgrType;
    private Type? _zEntityType;
    private Type? _attrTypeEnum;
    private PropertyInfo? _mgrInstanceProperty;   // ZUtil.ZSingleton<TMgr>.Instance
    private PropertyInfo? _mainEntityProperty;    // ZEntityMgr.MainEntity / PlayerEntity
    private PropertyInfo? _modelProperty;         // ZEntity.Model
    private MethodInfo? _entityTryGetAttrInt;     // bool TryGetAttr<int>(EAttrType, out int)
    private MethodInfo? _entityGetAttrInt;        // int GetAttr<int>(EAttrType)
    private MethodInfo? _entityTryGetAttrLong;    // bool TryGetAttr<long>(EAttrType, out long)
    private MethodInfo? _entityGetAttrLong;       // long GetAttr<long>(EAttrType)
    private MethodInfo? _entityTryGetAttrFloat;   // bool TryGetAttr<float>(EAttrType, out float) — float-stored attrs (e.g. cd-reduction 11760)
    private MethodInfo? _entityTryGetAttrStringDef; // open-generic TryGetAttr for any T (used for string)
    private MethodInfo? _entityTryGetAttrString;    // closed TryGetAttr<string>(EAttrType, out string) — cached once
    private MethodInfo? _entityGetLuaOriginEnergy;  // ZEntity.GetLuaOriginEnergy()
    private MethodInfo? _entityGetLuaMaxOriEnergy;  // ZEntity.GetLuaMaxOriEnergy()
    private MethodInfo? _modelGetAttrGoPosition;  // ZModel.GetAttrGoPosition() -> Vector3
    private bool _modelGetAttrGoPositionResolved;  // gate to avoid re-resolving every frame
    private FieldInfo? _vec3FieldX;
    private FieldInfo? _vec3FieldY;
    private FieldInfo? _vec3FieldZ;
    private PropertyInfo? _vec3PropX;
    private PropertyInfo? _vec3PropY;
    private PropertyInfo? _vec3PropZ;
    private bool _vec3MembersResolved;

    // Cached enum values pulled from Zproto.EAttrType.
    private object? _attrName;
    private object? _attrLevel;
    private object? _attrProfession;
    private object? _attrHp;
    private object? _attrMaxHp;
    private object? _attrOriginEnergy;
    private object? _attrMaxOriginEnergy;
    private bool _firstProfessionLogged;

    // Reusable invocation buffers + pre-boxed default values to avoid per-frame allocs
    // on the ~60 Hz player-state probe path. Single-threaded (Unity main thread), so
    // these are safe to mutate in place between calls.
    private readonly object?[] _args1 = new object?[1];
    private readonly object?[] _args2 = new object?[2];
    private static readonly object BoxedZeroInt = 0;
    private static readonly object BoxedZeroLong = 0L;
    private static readonly object BoxedZeroFloat = 0f;
    private static readonly object?[] EmptyArgs = Array.Empty<object?>();

    private bool _bootstrapped;
    private bool _firstSuccessLogged;
    private bool _firstFailureLogged;

    // Memo of which TryGetAttr<T> variant works for each attribute key.
    // true => long; false => int. Calling the WRONG variant triggers a
    // `[Error : Unity] arr type err, type=Int32, enum=AttrHp` log line
    // from the game's attribute storage on every call — at 60 Hz that's
    // 60 error lines per attribute per second. After the first
    // successful read we lock in the right type for that key.
    private readonly Dictionary<object, bool> _attrPrefersLong = new();

    public PandaPlayerStateProbe(
        IPluginLog log,
        IGameTypeRegistry typeRegistry,
        PandaCharIdentityReader? charIdentityReader = null)
    {
        _log = log;
        _typeRegistry = typeRegistry;
        _charIdentityReader = charIdentityReader;
    }

    public bool TrySample(out PlayerStateSnapshot snapshot)
    {
        snapshot = default;

        // Drop last tick's entity BEFORE anything else — sibling probes must never
        // receive a reference the game may have destroyed since. See _tickGoodEntity.
        _tickGoodEntity = null;

        if (!EnsureBootstrap())
        {
            return false;
        }

        var mgr = ReadSingletonInstance();
        if (mgr is null)
        {
            return false;
        }

        var entity = ReadEntity(mgr);
        if (entity is null)
        {
            return false;
        }

        var candidate = CaptureSnapshot(entity);

        // Consider the probe "succeeded" once we have at least *something* meaningful
        // beyond defaults — otherwise a half-loaded entity (no attrs yet) would
        // flap IsAvailable=true with all-zeros and confuse plugins.
        if (IsUseful(candidate))
        {
            NoteUsefulness(mgr, entity, useful: true, via: "MainEntity");
            _tickGoodEntity = entity;   // same-tick handoff for sibling probes
            snapshot = candidate;
            LogFirstSuccess(snapshot);
            return true;
        }

        return TrySampleAfterBlackout(mgr, entity, candidate, out snapshot);
    }

    // The manager's single playerEnt_ field went dark — reproduced by simply
    // MOUNTING (owner toggle 2026-07-29: mounted => hp 0, dismount => hp back).
    // Re-look-up the local player by uuid / char id and accept a replacement ONLY
    // if it validates as us. Strictly additive: a healthy entity never reaches
    // here, so position/replay behaviour on the normal path is unchanged.
    private bool TrySampleAfterBlackout(
        object mgr, object entity, in PlayerStateSnapshot dark, out PlayerStateSnapshot snapshot)
    {
        snapshot = default;

        if (TryRescueSample(mgr, out var rescued, out var via))
        {
            NoteUsefulness(mgr, entity, useful: true, via: via);
            snapshot = rescued;
            LogFirstSuccess(snapshot);
            return true;
        }

        NoteUsefulness(mgr, entity, useful: false, via: "none");
        if (!_firstFailureLogged)
        {
            _firstFailureLogged = true;
            _log.Info($"[PlayerState] probe found entity {entity.GetType().FullName} but no useful attrs yet (hp={dark.Health}, stamina={dark.Stamina}, lvl={dark.Level}, name='{dark.Name}')");
            DumpDiagnostics(entity);
        }
        return false;
    }

    // Shared usefulness predicate — the probe's definition of "this entity is
    // readable". Extracted so the rescue path applies the identical bar.
    private static bool IsUseful(in PlayerStateSnapshot s)
        => s.MaxHealth > 0 || s.Level > 0 || !string.IsNullOrEmpty(s.Name);

    private void LogFirstSuccess(in PlayerStateSnapshot snapshot)
    {
        if (_firstSuccessLogged) return;
        _firstSuccessLogged = true;
        _log.Info($"[PlayerState] first sample: name='{snapshot.Name}' lvl={snapshot.Level} prof={snapshot.Profession} hp={snapshot.Health}/{snapshot.MaxHealth} stamina={snapshot.Stamina}/{snapshot.MaxStamina} pos=({snapshot.Position.X:0.0},{snapshot.Position.Y:0.0},{snapshot.Position.Z:0.0})");
    }

    private PlayerStateSnapshot CaptureSnapshot(object entity)
    {
        var name = TryReadString(entity, _attrName);
        var level = TryReadInt(entity, _attrLevel);
        var profession = TryReadInt(entity, _attrProfession);
        var hp = TryReadInt(entity, _attrHp);
        var maxHp = TryReadInt(entity, _attrMaxHp);
        // The "origin energy" Panda field IS Star Resonance's stamina pool —
        // the Phase 9a rename of Mana → Stamina is purely the C# identifier
        // on our side; the wire mapping is unchanged. See
        // docs/superpowers/specs/2026-05-29-phase-9a-layout-primitives-design.md.
        var stamina = TryInvokeZeroArgInt(entity, _entityGetLuaOriginEnergy);
        if (stamina == 0)
        {
            stamina = TryReadInt(entity, _attrOriginEnergy);
        }
        var maxStamina = TryInvokeZeroArgInt(entity, _entityGetLuaMaxOriEnergy);
        if (maxStamina == 0)
        {
            maxStamina = TryReadInt(entity, _attrMaxOriginEnergy);
        }
        var pos = TryReadPosition(entity);

        if (profession != 0 && !_firstProfessionLogged)
        {
            _firstProfessionLogged = true;
            _log.Info($"[PlayerState] first profession id: {profession}");
        }

        return new PlayerStateSnapshot
        {
            Name = name,
            Level = level,
            Profession = profession,
            Health = hp,
            MaxHealth = maxHp,
            Stamina = stamina,
            MaxStamina = maxStamina,
            Position = pos,
        };
    }
}
