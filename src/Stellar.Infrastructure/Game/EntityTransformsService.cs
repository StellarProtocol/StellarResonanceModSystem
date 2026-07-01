using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reads entity world transforms (position + facing) by id via the game entity manager.
/// Reflects into <c>Panda.ZGame.ZEntityMgr</c> (the same singleton the player-state probe
/// uses) and walks: manager → <c>GetEntity(long)</c> → <c>ZEntity.Model</c> →
/// <c>GetAttrGoPosition()</c> + <c>GetAttrGoRotation()</c>.
/// All reflection handles are cached on first use; every failure path returns false.
/// Main-thread only — must be called from the framework Update tick.
/// </summary>
internal sealed class EntityTransformsService : IEntityTransforms
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic   = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // Recon-confirmed type + method names (replay-entity-transform-notes.md).
    private const string ManagerTypeName    = "Panda.ZGame.ZEntityMgr";

    private readonly IGameTypeRegistry _typeRegistry;

    // Cached reflection handles — resolved once, reused on every call.
    private bool _resolved;
    private PropertyInfo? _mgrInstanceProperty;  // ZUtil.ZSingleton<ZEntityMgr>.Instance
    private MethodInfo?   _getEntityMethod;      // ZEntityMgr.GetEntity(long uuid) → ZEntity
    private PropertyInfo? _modelProperty;        // ZEntity.Model → ZModel

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

    public EntityTransformsService(IGameTypeRegistry typeRegistry)
    {
        _typeRegistry = typeRegistry;
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
        return true;
    }

    // -------------------------------------------------------------------------
    // Bootstrap
    // -------------------------------------------------------------------------

    private void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }
        _resolved = true;
        try
        {
            var mgrType = _typeRegistry.FindType(ManagerTypeName);
            if (mgrType is null)
            {
                return;
            }

            // Singleton instance: ZUtil.ZSingleton<ZEntityMgr>.Instance
            _mgrInstanceProperty = FindSingletonInstanceProperty(mgrType);

            // GetEntity(long uuid) — bind by parameter types to pick the right overload.
            _getEntityMethod = mgrType.GetMethod(
                "GetEntity",
                AnyInstance,
                binder: null,
                types: new[] { typeof(long) },
                modifiers: null);
        }
        catch
        {
            // Leave unresolved — TryGetTransform returns false.
        }
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
            _modelProperty ??= entity.GetType().GetProperty("Model", AnyInstance);
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
