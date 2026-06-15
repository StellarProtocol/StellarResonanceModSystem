using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaPlayerStateProbe
{
    private object? ReadSingletonInstance()
    {
        if (_mgrInstanceProperty is null)
        {
            return null;
        }
        try
        {
            return _mgrInstanceProperty.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private object? ReadEntity(object mgr)
    {
        if (_mainEntityProperty is null)
        {
            return null;
        }
        try
        {
            return _mainEntityProperty.GetValue(mgr);
        }
        catch
        {
            return null;
        }
    }

    private int TryReadInt(object entity, object? attrKey)
    {
        if (attrKey is null)
        {
            return 0;
        }

        // Storage-type memo: after the first successful read we lock in the
        // matching TryGetAttr<T>. Calling the wrong T triggers the game's
        // `[Error : Unity] arr type err` logger at 60 Hz per attribute.
        if (_attrPrefersLong.TryGetValue(attrKey, out var prefersLong))
        {
            return prefersLong
                ? ReadAsLong(entity, attrKey)
                : ReadAsInt(entity, attrKey);
        }

        // First read for this key — probe long first (HP / MaxHp / Mp /
        // MaxMp are stored as long on Star Resonance) and fall back to int
        // (Level family). Memoize whichever wins so the next 60 Hz call
        // bypasses the wrong branch.
        var longResult = ReadAsLong(entity, attrKey, out var longHit);
        if (longHit)
        {
            _attrPrefersLong[attrKey] = true;
            return longResult;
        }

        var intResult = ReadAsInt(entity, attrKey, out var intHit);
        if (intHit)
        {
            _attrPrefersLong[attrKey] = false;
            return intResult;
        }

        return 0;
    }

    private int ReadAsLong(object entity, object attrKey)
        => ReadAsLong(entity, attrKey, out _);

    private readonly record struct AttrInvocation(MethodInfo? Method, object Entity, object AttrKey, object? BoxedDefault, bool UseTry);

    private int ReadAsLong(object entity, object attrKey, out bool hit)
    {
        hit = false;
        if (TryConvertAttrValue(new AttrInvocation(_entityTryGetAttrLong, entity, attrKey, BoxedZeroLong, UseTry: true), out var viaTry))
        {
            hit = true;
            return viaTry;
        }
        if (TryConvertAttrValue(new AttrInvocation(_entityGetAttrLong, entity, attrKey, null, UseTry: false), out var viaGet))
        {
            hit = true;
            return viaGet;
        }
        return 0;
    }

    /// <summary>
    /// Invokes either <c>TryGetAttr&lt;long&gt;</c> (when <see cref="AttrInvocation.UseTry"/> is true) or
    /// <c>GetAttr&lt;long&gt;</c> against the entity in <paramref name="inv"/>, unpacks the resulting boxed
    /// long, and reports success via <paramref name="value"/>. Returns false on missing method,
    /// invocation failure, or non-long result.
    /// </summary>
    private bool TryConvertAttrValue(AttrInvocation inv, out int value)
    {
        value = 0;
        if (inv.Method is null)
        {
            return false;
        }
        try
        {
            if (inv.UseTry)
            {
                _args2[0] = inv.AttrKey;
                _args2[1] = inv.BoxedDefault;
                var ok = inv.Method.Invoke(inv.Entity, _args2);
                if (ok is true && _args2[1] is long v)
                {
                    value = unchecked((int)v);
                    return true;
                }
            }
            else
            {
                _args1[0] = inv.AttrKey;
                var raw = inv.Method.Invoke(inv.Entity, _args1);
                if (raw is long l)
                {
                    value = unchecked((int)l);
                    return true;
                }
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private int ReadAsInt(object entity, object attrKey)
        => ReadAsInt(entity, attrKey, out _);

    private int ReadAsInt(object entity, object attrKey, out bool hit)
    {
        hit = false;
        if (_entityTryGetAttrInt is not null)
        {
            try
            {
                _args2[0] = attrKey;
                _args2[1] = BoxedZeroInt;
                var ok = _entityTryGetAttrInt.Invoke(entity, _args2);
                if (ok is true && _args2[1] is int v)
                {
                    hit = true;
                    return v;
                }
            }
            catch { /* fall through */ }
        }
        if (_entityGetAttrInt is not null)
        {
            try
            {
                _args1[0] = attrKey;
                var v = _entityGetAttrInt.Invoke(entity, _args1);
                if (v is int i)
                {
                    hit = true;
                    return i;
                }
            }
            catch { /* ignore */ }
        }
        return 0;
    }

    private static int TryInvokeZeroArgInt(object instance, MethodInfo? method)
    {
        if (method is null)
        {
            return 0;
        }
        try
        {
            var v = method.Invoke(instance, EmptyArgs);
            return v switch
            {
                int i => i,
                long l => unchecked((int)l),
                float f => (int)f,
                double d => (int)d,
                _ => 0,
            };
        }
        catch
        {
            return 0;
        }
    }

    private string? TryReadString(object entity, object? attrKey)
    {
        if (attrKey is null || _entityTryGetAttrString is null)
        {
            return null;
        }
        try
        {
            _args2[0] = attrKey;
            _args2[1] = null;
            var ok = _entityTryGetAttrString.Invoke(entity, _args2);
            if (ok is true)
            {
                return _args2[1] as string;
            }
        }
        catch
        {
            // Some attr types reject the string projection; that's fine.
        }
        return null;
    }

    private Position3D TryReadPosition(object entity)
    {
        if (_modelProperty is null)
        {
            return Position3D.Zero;
        }
        object? model;
        try
        {
            model = _modelProperty.GetValue(entity);
        }
        catch
        {
            return Position3D.Zero;
        }
        if (model is null)
        {
            return Position3D.Zero;
        }

        ResolvePositionAccessor(model);
        if (_modelGetAttrGoPosition is null)
        {
            return Position3D.Zero;
        }

        try
        {
            var v = _modelGetAttrGoPosition.Invoke(model, EmptyArgs);
            return UnpackVector3(v);
        }
        catch
        {
            return Position3D.Zero;
        }
    }

    /// <summary>
    /// Resolve <c>ZModel.GetAttrGoPosition()</c> lazily — runtime type may differ from the
    /// declared property type. After the first successful resolve, skip the type-comparison
    /// + re-lookup; the model type is stable post-bootstrap, so we avoid the per-frame
    /// <c>GetMethod</c> cost.
    /// </summary>
    private void ResolvePositionAccessor(object model)
    {
        if (_modelGetAttrGoPositionResolved)
        {
            return;
        }
        _modelGetAttrGoPosition = model.GetType().GetMethod("GetAttrGoPosition", AnyInstance, binder: null, types: System.Type.EmptyTypes, modifiers: null);
        _modelGetAttrGoPositionResolved = true;
    }

    private Position3D UnpackVector3(object? v)
    {
        if (v is null)
        {
            return Position3D.Zero;
        }
        // Cache the x/y/z accessors on the first sample — the boxed Vector3 type is
        // stable, so we never need to re-resolve after the first non-null sample.
        if (!_vec3MembersResolved)
        {
            var t = v.GetType();
            _vec3FieldX = t.GetField("x");
            _vec3FieldY = t.GetField("y");
            _vec3FieldZ = t.GetField("z");
            if (_vec3FieldX is null)
            {
                _vec3PropX = t.GetProperty("x");
            }
            if (_vec3FieldY is null)
            {
                _vec3PropY = t.GetProperty("y");
            }
            if (_vec3FieldZ is null)
            {
                _vec3PropZ = t.GetProperty("z");
            }
            _vec3MembersResolved = true;
        }
        var x = _vec3FieldX is not null ? _vec3FieldX.GetValue(v) : _vec3PropX?.GetValue(v);
        var y = _vec3FieldY is not null ? _vec3FieldY.GetValue(v) : _vec3PropY?.GetValue(v);
        var z = _vec3FieldZ is not null ? _vec3FieldZ.GetValue(v) : _vec3PropZ?.GetValue(v);
        return new Position3D(ToFloat(x), ToFloat(y), ToFloat(z));
    }

    private static float ToFloat(object? o) => o switch
    {
        float f => f,
        double d => (float)d,
        int i => i,
        long l => l,
        _ => 0f,
    };

    private void DumpDiagnostics(object entity)
    {
        try
        {
            var props = entity.GetType()
                .GetProperties(AnyInstance)
                .Select(p => p.Name)
                .OrderBy(n => n)
                .Take(40)
                .ToArray();
            _log.Info($"[PlayerState] entity properties: {string.Join(", ", props)}");
        }
        catch
        {
            // diagnostics must not break the probe
        }
    }
}
