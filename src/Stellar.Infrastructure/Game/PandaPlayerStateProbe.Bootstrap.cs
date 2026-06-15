using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaPlayerStateProbe
{
    private bool EnsureBootstrap()
    {
        if (_bootstrapped)
        {
            return _zEntityMgrType is not null && _zEntityType is not null;
        }

        _zEntityMgrType = _typeRegistry.FindType("Panda.ZGame.ZEntityMgr");
        _zEntityType = _typeRegistry.FindType("Panda.ZGame.ZEntity");
        _attrTypeEnum = _typeRegistry.FindType("Zproto.EAttrType");

        if (_zEntityMgrType is null || _zEntityType is null)
        {
            // hot-update assemblies not loaded yet — retry next tick.
            return false;
        }

        // Mark bootstrapped before logging so we don't spam on retry.
        _bootstrapped = true;

        // Resolve singleton: ZUtil.ZSingleton<ZEntityMgr>.Instance is the canonical
        // accessor — find it by scanning loaded assemblies for the closed generic.
        _mgrInstanceProperty = FindSingletonInstanceProperty(_zEntityMgrType);
        if (_mgrInstanceProperty is null)
        {
            _log.Warning($"[PlayerState] ZUtil.ZSingleton<{_zEntityMgrType.FullName}>.Instance not found; cannot reach manager");
            return false;
        }

        // Resolve MainEntity / PlayerEntity property on the manager.
        if (!ResolveMainEntityProperty())
        {
            return false;
        }

        _modelProperty = _zEntityType.GetProperty("Model", AnyInstance);

        // Resolve attribute enum values once.
        BuildEnumValueCache();

        // Discover the simplest int-shaped GetAttr / TryGetAttr overload on ZEntity.
        ResolveAttrAccessors();

        _log.Info($"[PlayerState] bootstrap ok: mgr={_zEntityMgrType.FullName} entity={_zEntityType.FullName} attrEnum={_attrTypeEnum?.FullName ?? "(missing)"} TryGetAttr<int>={(_entityTryGetAttrInt is not null)} TryGetAttr<long>={(_entityTryGetAttrLong is not null)} GetAttr<int>={(_entityGetAttrInt is not null)} GetAttr<long>={(_entityGetAttrLong is not null)}");
        return true;
    }

    // Resolve the MainEntity / PlayerEntity property on the manager. Returns
    // false (and warns) when the manager type exposes none of the recon-known
    // accessor names — bootstrap cannot proceed without one.
    private bool ResolveMainEntityProperty()
    {
        _mainEntityProperty = _zEntityMgrType!.GetProperty("MainEntity", AnyInstance)
            ?? _zEntityMgrType.GetProperty("PlayerEntity", AnyInstance)
            ?? _zEntityMgrType.GetProperty("MainEnt", AnyInstance)
            ?? _zEntityMgrType.GetProperty("PlayerEnt", AnyInstance);
        if (_mainEntityProperty is null)
        {
            _log.Warning($"[PlayerState] no MainEntity/PlayerEntity property on {_zEntityMgrType.FullName}");
            return false;
        }
        return true;
    }

    private void BuildEnumValueCache()
    {
        if (_attrTypeEnum is null || !_attrTypeEnum.IsEnum)
        {
            _log.Warning("[PlayerState] Zproto.EAttrType enum not found; attr lookups will fail");
            return;
        }

        _attrName = TryParseEnum(_attrTypeEnum, "AttrName");
        _attrLevel = TryParseEnum(_attrTypeEnum, "AttrRoleLevel")
            ?? TryParseEnum(_attrTypeEnum, "AttrLevel");
        _attrProfession = TryParseEnum(_attrTypeEnum, "AttrProfessionId")
            ?? TryParseEnum(_attrTypeEnum, "AttrProfession");
        _attrHp = TryParseEnum(_attrTypeEnum, "AttrHp");
        _attrMaxHp = TryParseEnum(_attrTypeEnum, "AttrMaxHpTotal")
            ?? TryParseEnum(_attrTypeEnum, "AttrMaxHp");
        _attrOriginEnergy = TryParseEnum(_attrTypeEnum, "AttrOriginEnergy");
        _attrMaxOriginEnergy = TryParseEnum(_attrTypeEnum, "AttrMaxOriginEnergyTotal")
            ?? TryParseEnum(_attrTypeEnum, "AttrMaxOriginEnergy");

        // Pre-seed the storage-type memo from observed game behavior.
        // Without this seed the FIRST read of each attribute tries the
        // wrong T once and the game emits a single `[Error : Unity] arr
        // type err` line. Pre-seeding skips that one-shot noise too.
        //
        // Observed types (per the game's own attribute-storage error
        // messages):
        //   AttrRoleLevel              → Int32
        //   AttrHp / AttrMaxHpTotal    → Int64
        //   AttrOriginEnergy /
        //   AttrMaxOriginEnergyTotal   → Int64
        if (_attrLevel              is not null) _attrPrefersLong[_attrLevel]              = false;
        // AttrProfessionId is an int32 profession code (see Zproto.EAttrType recon).
        if (_attrProfession         is not null) _attrPrefersLong[_attrProfession]         = false;
        if (_attrHp                 is not null) _attrPrefersLong[_attrHp]                 = true;
        if (_attrMaxHp              is not null) _attrPrefersLong[_attrMaxHp]              = true;
        if (_attrOriginEnergy       is not null) _attrPrefersLong[_attrOriginEnergy]       = true;
        if (_attrMaxOriginEnergy    is not null) _attrPrefersLong[_attrMaxOriginEnergy]    = true;
    }

    private void ResolveAttrAccessors()
    {
        ResolveGetAttrOverloads();

        // ZEntity exposes dedicated zero-arg helpers for OriginEnergy (the in-game
        // stamina/energy used by skills + parkour). They bypass the generic attr
        // table — we saw GetLuaOriginEnergy / GetLuaMaxOriEnergy in the wrap surface.
        if (_zEntityType is null)
        {
            return;
        }
        _entityGetLuaOriginEnergy = _zEntityType.GetMethod("GetLuaOriginEnergy", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        _entityGetLuaMaxOriEnergy = _zEntityType.GetMethod("GetLuaMaxOriEnergy", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);

        // ZModel.GetAttrGoPosition() -> Vector3 lives in Panda.ZGame.ZModel — find the
        // method on whatever type the Model property returns. Resolution deferred until
        // we have a live entity (the runtime type may differ from the declared property type).
    }

    private static PropertyInfo? FindSingletonInstanceProperty(Type tMgr)
    {
        // Look for ZUtil.ZSingleton`1[T] closed-generic with T=tMgr; grab static Instance.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singleton = null;
            try
            {
                singleton = assembly.GetType("ZUtil.ZSingleton`1", throwOnError: false);
            }
            catch
            {
                continue;
            }
            if (singleton is null)
            {
                continue;
            }
            try
            {
                var closed = singleton.MakeGenericType(tMgr);
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

    private static object? TryParseEnum(Type enumType, string name)
    {
        try
        {
            if (Enum.IsDefined(enumType, name))
            {
                return Enum.Parse(enumType, name);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private void ResolveGetAttrOverloads()
    {
        if (_zEntityType is null || _attrTypeEnum is null)
        {
            return;
        }

        var openTryGetAttr = MatchGenericOverload(_zEntityType, "TryGetAttr", expectedParamCount: 2, requireLastOut: true);
        if (openTryGetAttr is not null)
        {
            try { _entityTryGetAttrInt = openTryGetAttr.MakeGenericMethod(typeof(int)); } catch { }
            try { _entityTryGetAttrLong = openTryGetAttr.MakeGenericMethod(typeof(long)); } catch { }
            try { _entityTryGetAttrFloat = openTryGetAttr.MakeGenericMethod(typeof(float)); } catch { }
            _entityTryGetAttrStringDef = openTryGetAttr; // keep the open def for string lookup
            // Close <string> once at bootstrap so the per-frame TryReadString path doesn't
            // re-run MakeGenericMethod on every invocation.
            try { _entityTryGetAttrString = openTryGetAttr.MakeGenericMethod(typeof(string)); } catch { }
        }

        var openGetAttr = MatchGenericOverload(_zEntityType, "GetAttr", expectedParamCount: 1, requireLastOut: false);
        if (openGetAttr is not null)
        {
            try { _entityGetAttrInt = openGetAttr.MakeGenericMethod(typeof(int)); } catch { }
            try { _entityGetAttrLong = openGetAttr.MakeGenericMethod(typeof(long)); } catch { }
        }
    }

    private MethodInfo? MatchGenericOverload(Type declaringType, string methodName, int expectedParamCount, bool requireLastOut)
    {
        foreach (var m in declaringType.GetMethods(AnyInstance))
        {
            if (m.Name != methodName)
            {
                continue;
            }
            if (!m.IsGenericMethodDefinition)
            {
                continue;
            }
            if (m.GetGenericArguments().Length != 1)
            {
                continue;
            }
            var ps = m.GetParameters();
            if (ps.Length != expectedParamCount)
            {
                continue;
            }
            if (ps[0].ParameterType != _attrTypeEnum)
            {
                continue;
            }
            if (requireLastOut && !ps[ps.Length - 1].IsOut)
            {
                continue;
            }
            return m;
        }
        return null;
    }
}
