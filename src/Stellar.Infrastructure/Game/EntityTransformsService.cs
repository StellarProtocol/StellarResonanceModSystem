using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

// Diagnostics live in EntityTransformsService.Diagnostics.cs (per-event gated on
// StellarDiagnostics + one ungated one-shot the first time the fallback engages).

/// <summary>
/// Reads entity world transforms (position + facing) by id via the game entity manager.
/// Reflects into <c>Panda.ZGame.ZEntityMgr</c> (the same singleton the player-state probe
/// uses) and walks: manager → <c>GetEntity(long)</c> → <c>ZEntity.Model</c> →
/// <c>GetAttrGoPosition()</c> + <c>GetAttrGoRotation()</c>.
/// All reflection handles are cached on first use; every failure path returns false.
/// Main-thread only — must be called from the framework Update tick.
///
/// <para>The GameObject transform is the RENDERED view, which streams ≈(0,0,0) for ~7 s after a
/// silent intra-scene teleport while the entity is alive and fighting (see
/// <c>docs/recon/thanatos-walkin-geo.md</c>). When that read hits the zero sentinel this service
/// falls back to <see cref="WireEntityPositions"/> — the server-synced <c>AttrPos(52)</c> logic
/// position cached off the AOI delta stream — which is crash-proof (a managed read of parsed packet
/// bytes, never a live IL2CPP deref; see <c>docs/il2cpp-probing-safety.md</c> §4).</para>
/// </summary>
internal sealed partial class EntityTransformsService : IEntityTransforms
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic   = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // Recon-confirmed type + method names (replay-entity-transform-notes.md).
    private const string ManagerTypeName    = "Panda.ZGame.ZEntityMgr";

    // Max age of a cached wire position that may substitute for the zero-sentinel view read.
    // AOI position deltas arrive at roughly the 2 Hz cadence the replay capture samples at, and the
    // silent-teleport view-settle window is ~7 s; 5 s comfortably covers a few missed deltas plus the
    // settle window, while old enough that an entity that has genuinely stopped receiving position
    // updates (idle / left the moving set) is NOT resurrected with a phantom position.
    // HONEST BOUNDARY: whether AttrPos actually refreshes DURING the un-settled window is UNVERIFIED
    // offline (thanatos-walkin-geo.md); the owner's raid run is the proof gate. If it does not refresh,
    // the cached sample ages past this bound and the fallback changes nothing — fails safe either way.
    internal const long WirePositionMaxStaleMs = 5_000;

    private readonly IGameTypeRegistry _typeRegistry;
    private readonly WireEntityPositions _positions;
    private readonly IPluginLog _log;

    // Cached reflection handles — resolved lazily, retried until all are non-null.
    // Do NOT add a permanent "resolved" bool; the guard is handle-presence so that
    // a failed attempt (Panda.* not loaded yet) retries on the next tick (I-1).
    private PropertyInfo? _mgrInstanceProperty;  // ZUtil.ZSingleton<ZEntityMgr>.Instance
    private MethodInfo?   _getEntityMethod;      // ZEntityMgr.GetEntity(long uuid) → ZEntity
    private PropertyInfo? _modelProperty;        // ZEntity.Model → ZModel (resolved from abstract base, I-2)

    // Position accessor — resolved from the live model type on first non-null model.
    private MethodInfo? _getPosition;
    private bool        _positionResolved;

    // Rotation accessor — resolved from the live model type on first non-null model.
    private MethodInfo? _getRotation;
    private bool        _rotationResolved;

    // Vector3 field/prop handles — cached on first non-null position vector.
    private FieldInfo?   _vec3FieldX;
    private FieldInfo?   _vec3FieldY;
    private FieldInfo?   _vec3FieldZ;
    private PropertyInfo? _vec3PropX;
    private PropertyInfo? _vec3PropY;
    private PropertyInfo? _vec3PropZ;
    private bool _vec3Resolved;

    // Quaternion eulerAngles handle — cached on first non-null rotation quaternion.
    private PropertyInfo? _quatEulerAngles;
    private bool          _quatResolved;

    // Reusable single-element arg buffer — avoids per-call alloc on the call path.
    private readonly object[] _arg1 = new object[1];

    private static readonly object?[] EmptyArgs = Array.Empty<object?>();

    public EntityTransformsService(IGameTypeRegistry typeRegistry, WireEntityPositions positions, IPluginLog log)
    {
        _typeRegistry = typeRegistry;
        _positions    = positions ?? throw new ArgumentNullException(nameof(positions));
        _log          = log       ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc/>
    public bool TryGetTransform(EntityId id, out Position3D position, out float yawDegrees)
    {
        position = Position3D.Zero;
        yawDegrees = 0f;

        EnsureResolved();
        if (_mgrInstanceProperty is null || _getEntityMethod is null)
        {
            return false;
        }

        var entity = ResolveEntity(id);
        if (entity is null)
        {
            return false;
        }

        var model = ReadModel(entity);
        if (model is null)
        {
            return false;
        }

        position = ReadPosition(model);
        yawDegrees = ReadYawDegrees(model);

        // View-transform lag fix (run sea/223237664013287424). The GO transform above is the RENDERED
        // view, which reads DEGENERATE for the entity's first ~7 s in a silently-teleported scene.
        // Substitute a FRESH wire position when it does. GO-read FAILURES (entity/model null → false
        // returns above) are untouched; when no fresh wire exists we keep the GO read exactly as today.
        MaybeApplyWireFallback(id, ref position, ref yawDegrees);
        return true;
    }

    // Zero-sentinel predicate — duplicated from the CombatMeter plugin (commit 1ff4564) with the SAME
    // justification: the map origin is INTERIOR in 518/609 maps, so world (0,0) can be a real in-bounds
    // coordinate — Y is the discriminator. A real play floor is Y≈100 (raid) and never exactly 0, while a
    // resolved-but-unpopulated GO transform reads Y==0 exactly. So (Y==0f AND |X|,|Z|<0.5) means "view not
    // yet populated", not a real position.
    internal static bool IsZeroSentinel(in Position3D p)
        => p.Y == 0f && MathF.Abs(p.X) < 0.5f && MathF.Abs(p.Z) < 0.5f;

    // Max vertical (floor) disagreement, in metres, tolerated between the GO read and a fresh wire
    // sample before the wire is preferred. See ShouldSubstituteFreshWire.
    internal const float WireFloorDisagreeM = 5f;

    // Substitution decision given the GO read and a FRESH wire position. Two DEGENERATE GO shapes were
    // measured during the silent-teleport un-settled window (run sea/223237664013287424): (a) the exact
    // zero sentinel (resolved-but-unpopulated view), AND (b) NEAR-ORIGIN JITTER (X 0→37, Y≈0, Z ±14) —
    // NOT exact zeros, so predicate (a) alone never matched and the good wire cache went unused
    // ([PosDbg][fallback] count = 0 over that whole run). The reliable tell for BOTH is VERTICAL floor
    // disagreement: the degenerate GO sits at Y≈0 while the true floor is Y≈100 (~100 m measured;
    // settled agreement is < 5 m). A vertical threshold avoids the false positive a horizontal-distance
    // trigger would hit during a fast dash — the wire is ≤ 5 s stale at ~2 Hz, so a dash can exceed 10 m
    // HORIZONTALLY between updates, but a persistent 5 m+ FLOOR gap only happens when the GO view is
    // degenerate. GO stays primary whenever it agrees vertically with fresh wire.
    internal static bool ShouldSubstituteFreshWire(in Position3D goPos, in Position3D wirePos)
    {
        if (IsZeroSentinel(wirePos)) return false;             // degenerate wire entry — never a second zero
        if (IsZeroSentinel(goPos)) return true;                // (a) exact zero sentinel
        return MathF.Abs(goPos.Y - wirePos.Y) > WireFloorDisagreeM; // (b) near-origin jitter → Y-floor disagreement
    }

    internal void MaybeApplyWireFallback(EntityId id, ref Position3D position, ref float yawDegrees)
    {
        // Fetch the fresh wire entry FIRST — the trigger compares GO against it, not just the zero shape.
        if (!_positions.TryGetFresh(id.Value, WirePositionMaxStaleMs, out var s))
        {
            DiagWireFallbackSkip(id, position, wireHit: false, default, "no-fresh-wire");
            return;
        }
        var wirePos = new Position3D(s.X, s.Y, s.Z);
        // WIRE-FIRST (owner-confirmed run sea/i3yeDnkRla: the GO view is degenerate for the ENTIRE
        // pre-archive window — not a brief settle). The replay capture is the ONLY consumer of this
        // service, and the server-synced AttrPos IS the authoritative logic position, so prefer a fresh,
        // non-degenerate wire outright instead of gating on the GO-vs-wire heuristic (ShouldSubstitute-
        // FreshWire), which silently skipped on the owner's runs despite a fresh Y=100 wire + near-origin
        // GO. GO is kept only when the wire itself is unusable, so a fully-settled run is unaffected
        // (wire ≈ GO) and the previously-working case still substitutes. See ShouldSubstituteFreshWire's
        // doc for the old heuristic (retained + unit-tested for reference).
        if (IsZeroSentinel(wirePos))
        {
            DiagWireFallbackSkip(id, position, wireHit: true, s, "wire-degenerate");
            return;
        }
        position = wirePos;
        if (s.HasYaw) yawDegrees = NormalizeYaw(s.Yaw);
        DiagWireFallbackEngaged(id, s.AgeMs);
    }

    // Facing from the wire (AttrPos.dir / AttrDir). UNIT UNVERIFIED offline (degrees vs radians) — the GO
    // path yields eulerAngles.y in DEGREES; the [PosDbg] diag logs the raw wire dir so the owner run can
    // confirm the unit. Normalise into [0,360) to match the GO path's output contract regardless; position
    // (not facing) is the load-bearing datum here.
    private static float NormalizeYaw(float raw)
    {
        var y = raw % 360f;
        return y < 0f ? y + 360f : y;
    }

    // -------------------------------------------------------------------------
    // Bootstrap
    // -------------------------------------------------------------------------

    // I-1: guard is handle-presence, not a permanent bool, so failed attempts retry
    // on the next tick (Panda.* hot-update assemblies may not be loaded yet).
    private void EnsureResolved()
    {
        if (_mgrInstanceProperty is not null && _getEntityMethod is not null && _modelProperty is not null)
        {
            return;
        }
        try
        {
            TryResolveHandles();
        }
        catch
        {
            // Leave unresolved — TryGetTransform returns false; next tick will retry.
        }
    }

    // Attempts to resolve all three reflection handles atomically.
    // Returns without assigning if any prerequisite is absent (hot-update not loaded yet).
    private void TryResolveHandles()
    {
        var mgrType = _typeRegistry.FindType(ManagerTypeName);
        if (mgrType is null)
        {
            return;
        }

        // I-2: resolve Model from the abstract base type so it works for every
        // ZEntity subclass, not just the first concrete type encountered.
        var entityType = _typeRegistry.FindType("Panda.ZGame.ZEntity");
        if (entityType is null)
        {
            return;
        }

        // Singleton instance: ZUtil.ZSingleton<ZEntityMgr>.Instance
        var instanceProp = FindSingletonInstanceProperty(mgrType);
        if (instanceProp is null)
        {
            return;
        }

        // GetEntity(long uuid) — bind by parameter types to pick the right overload.
        var getEntity = mgrType.GetMethod(
            "GetEntity",
            AnyInstance,
            binder: null,
            types: new[] { typeof(long) },
            modifiers: null);
        if (getEntity is null)
        {
            return;
        }

        var modelProp = entityType.GetProperty("Model", AnyInstance);
        if (modelProp is null)
        {
            return;
        }

        // All handles resolved — publish atomically so the guard in EnsureResolved stays coherent.
        _mgrInstanceProperty = instanceProp;
        _getEntityMethod     = getEntity;
        _modelProperty       = modelProp;
    }

    private static PropertyInfo? FindSingletonInstanceProperty(Type tMgr)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singletonOpen = null;
            try
            {
                singletonOpen = assembly.GetType("ZUtil.ZSingleton`1", throwOnError: false);
            }
            catch
            {
                continue;
            }
            if (singletonOpen is null)
            {
                continue;
            }
            try
            {
                var closed = singletonOpen.MakeGenericType(tMgr);
                var prop = closed.GetProperty("Instance", AnyStatic);
                if (prop is not null)
                {
                    return prop;
                }
            }
            catch
            {
                // Try next assembly.
            }
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Entity resolution
    // -------------------------------------------------------------------------

    private object? ResolveEntity(EntityId id)
    {
        try
        {
            var mgr = _mgrInstanceProperty!.GetValue(null);
            if (mgr is null)
            {
                return null;
            }
            _arg1[0] = id.Value;
            return _getEntityMethod!.Invoke(mgr, _arg1);
        }
        catch
        {
            return null;
        }
    }

    private object? ReadModel(object entity)
    {
        try
        {
            // _modelProperty is resolved from the abstract ZEntity base in EnsureResolved (I-2);
            // it is never initialised here from the concrete instance type.
            return _modelProperty?.GetValue(entity);
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Position reading (mirrors PandaPlayerStateProbe.Read.cs)
    // -------------------------------------------------------------------------

    private Position3D ReadPosition(object model)
    {
        try
        {
            ResolvePositionAccessor(model);
            if (_getPosition is null)
            {
                return Position3D.Zero;
            }
            var v = _getPosition.Invoke(model, EmptyArgs);
            return UnpackVector3(v);
        }
        catch
        {
            return Position3D.Zero;
        }
    }

    private void ResolvePositionAccessor(object model)
    {
        if (_positionResolved)
        {
            return;
        }
        _positionResolved = true;
        _getPosition = model.GetType().GetMethod(
            "GetAttrGoPosition",
            AnyInstance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
    }

    private Position3D UnpackVector3(object? v)
    {
        if (v is null)
        {
            return Position3D.Zero;
        }
        if (!_vec3Resolved)
        {
            var t = v.GetType();
            _vec3FieldX = t.GetField("x");
            _vec3FieldY = t.GetField("y");
            _vec3FieldZ = t.GetField("z");
            if (_vec3FieldX is null) _vec3PropX = t.GetProperty("x");
            if (_vec3FieldY is null) _vec3PropY = t.GetProperty("y");
            if (_vec3FieldZ is null) _vec3PropZ = t.GetProperty("z");
            _vec3Resolved = true;
        }
        var x = _vec3FieldX is not null ? _vec3FieldX.GetValue(v) : _vec3PropX?.GetValue(v);
        var y = _vec3FieldY is not null ? _vec3FieldY.GetValue(v) : _vec3PropY?.GetValue(v);
        var z = _vec3FieldZ is not null ? _vec3FieldZ.GetValue(v) : _vec3PropZ?.GetValue(v);
        return new Position3D(ToFloat(x), ToFloat(y), ToFloat(z));
    }

    private static float ToFloat(object? o) => o switch
    {
        float f  => f,
        double d => (float)d,
        int i    => i,
        long l   => l,
        _        => 0f,
    };

    // -------------------------------------------------------------------------
    // Rotation / yaw reading
    // -------------------------------------------------------------------------

    private float ReadYawDegrees(object model)
    {
        try
        {
            ResolveRotationAccessor(model);
            if (_getRotation is null)
            {
                return 0f;
            }
            var q = _getRotation.Invoke(model, EmptyArgs);
            return UnpackQuaternionEulerY(q);
        }
        catch
        {
            return 0f;
        }
    }

    private void ResolveRotationAccessor(object model)
    {
        if (_rotationResolved)
        {
            return;
        }
        _rotationResolved = true;
        _getRotation = model.GetType().GetMethod(
            "GetAttrGoRotation",
            AnyInstance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
    }

    /// <summary>
    /// Unpacks a boxed <c>UnityEngine.Quaternion</c> and returns <c>eulerAngles.y</c>
    /// in degrees [0,360). Reads the <c>eulerAngles</c> Vector3 property in-managed
    /// to avoid a second reflected Unity call.
    /// </summary>
    private float UnpackQuaternionEulerY(object? q)
    {
        if (q is null)
        {
            return 0f;
        }
        try
        {
            if (!_quatResolved)
            {
                _quatEulerAngles = q.GetType().GetProperty("eulerAngles", AnyInstance);
                _quatResolved = true;
            }
            if (_quatEulerAngles is null)
            {
                return 0f;
            }
            var euler = _quatEulerAngles.GetValue(q);
            if (euler is null)
            {
                return 0f;
            }
            // Reuse the vec3 unpack handles if already resolved; otherwise unpack directly.
            var eulerVec = UnpackVector3(euler);
            var yaw = eulerVec.Y % 360f;
            return yaw < 0f ? yaw + 360f : yaw;
        }
        catch
        {
            return 0f;
        }
    }
}
