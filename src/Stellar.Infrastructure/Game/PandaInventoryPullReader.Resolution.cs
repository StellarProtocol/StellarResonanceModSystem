using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Singleton / accessor-resolution helpers for
/// <see cref="PandaInventoryPullReader"/>. Encapsulates the three strategies used
/// to reach a live <c>CharSerialize</c> instance from a static-context
/// reflection probe:
/// <list type="number">
///   <item><b>Strategy A</b> — any static field/property anywhere in the
///         loaded assemblies whose value type IS the CharSerialize type.
///         This catches the <c>_cachedCharSerialize</c> field per
///         <c>recon/phase-7-types.md</c> §1.</item>
///   <item><b>Strategy B</b> — any instance member of CharSerialize on a
///         class reachable via:
///         (i) Instance/Current/Singleton/Default static accessor;
///         (ii) <c>ZUtil.ZSingleton&lt;T&gt;.Instance</c> — the Phase 1 pattern;
///         (iii) any static field/property whose value type IS the class.</item>
/// </list>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private sealed class InstanceProvider
    {
        public InstanceProvider(string namePretty, Func<object?> read)
        {
            NamePretty = namePretty;
            Read = read;
        }
        public string NamePretty { get; }
        public Func<object?> Read { get; }
    }

    private static InstanceProvider? ResolveAnyInstanceProvider(Type owner)
    {
        // 1. Instance/Current/Singleton/Default property on owner.
        if (TryResolveStaticInstanceAccessor(owner, out var direct))
        {
            var d = direct;
            return new InstanceProvider(d.NamePretty, d.Read);
        }

        // 2. ZUtil.ZSingleton<owner>.Instance — the Phase 1 pattern.
        var zSingleton = ResolveZSingletonInstance(owner);
        if (zSingleton is not null) return zSingleton;

        // 3. Any static field/property on any type whose value type IS or
        //    DERIVES FROM owner — covers `static MyService s_svc` etc.
        var globalStatic = ResolveStaticHolderOfType(owner);
        if (globalStatic is not null) return globalStatic;

        return null;
    }

    private static MethodInfo? FindContainerResolveMethod(Type resolverType)
    {
        return resolverType.GetMethod("Resolve", new[] { typeof(Type) })
            ?? resolverType.GetMethod("Resolve", AnyInstance, binder: null,
                types: new[] { typeof(Type) }, modifiers: null);
    }

    // Finds a CharSerialize-typed member on a service instance type. Prefers a
    // property literally named `Data` (recon §1: get_Data_Public_get_CharSerialize),
    // then any CharSerialize-typed property, then a CharSerialize-typed field.
    private static CharSerializeMemberReader? TryBuildCharSerializeDataReader(Type serviceType, Type charSerializeType)
    {
        var dataProp = serviceType.GetProperty("Data", AnyInstance);
        if (dataProp is not null && IsCharSerializeCompatible(dataProp.PropertyType, charSerializeType))
        {
            var captured = dataProp;
            return new CharSerializeMemberReader(
                "Data (property)",
                owner => { try { return captured.GetValue(owner); } catch { return null; } });
        }
        return TryBuildInstanceCharSerializeMemberReader(serviceType, charSerializeType);
    }

    // Tolerant CharSerialize match for the container `.Data` reader. Accepts
    // exact identity OR assignability either way — guards against Il2CppInterop
    // surfacing a slightly different declared type than the resolved
    // CharSerialize handle.
    private static bool IsCharSerializeCompatible(Type declared, Type charSerializeType)
    {
        return declared == charSerializeType
            || charSerializeType.IsAssignableFrom(declared)
            || declared.IsAssignableFrom(charSerializeType);
    }

    private static InstanceProvider? ResolveZSingletonInstance(Type ownerType)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singleton = null;
            try { singleton = asm.GetType("ZUtil.ZSingleton`1", throwOnError: false); }
            catch { continue; }
            if (singleton is null) continue;
            try
            {
                var closed = singleton.MakeGenericType(ownerType);
                var prop = closed.GetProperty("Instance", AnyStatic);
                if (prop is not null)
                {
                    var captured = prop;
                    return new InstanceProvider(
                        $"ZUtil.ZSingleton<{ownerType.FullName}>.Instance",
                        () => { try { return captured.GetValue(null); } catch { return null; } });
                }
            }
            catch { /* try next assembly */ }
        }
        return null;
    }

    private static InstanceProvider? ResolveStaticHolderOfType(Type ownerType)
    {
        // Walk every assembly + class, look for any static field/property whose
        // declared type is exactly ownerType. This is the fallback for
        // bespoke service registries (e.g. a static `Holder.CharDataService`).
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = SafeAssemblyName(asm);
            if (ShouldSkipAssemblyForScan(asmName)) continue;
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;

                PropertyInfo[] sProps;
                try { sProps = t.GetProperties(AnyStatic); } catch { sProps = Array.Empty<PropertyInfo>(); }
                foreach (var p in sProps)
                {
                    Type ptype;
                    try { ptype = p.PropertyType; } catch { continue; }
                    if (ptype != ownerType) continue;
                    var captured = p;
                    return new InstanceProvider(
                        $"{t.FullName}.{captured.Name} (static property holder)",
                        () => { try { return captured.GetValue(null); } catch { return null; } });
                }

                FieldInfo[] sFields;
                try { sFields = t.GetFields(AnyStatic); } catch { sFields = Array.Empty<FieldInfo>(); }
                foreach (var f in sFields)
                {
                    Type ftype;
                    try { ftype = f.FieldType; } catch { continue; }
                    if (ftype != ownerType) continue;
                    var captured = f;
                    return new InstanceProvider(
                        $"{t.FullName}.{captured.Name} (static field holder)",
                        () => { try { return captured.GetValue(null); } catch { return null; } });
                }
            }
        }
        return null;
    }

    private static string SafeAssemblyName(System.Reflection.Assembly asm)
    {
        try { return asm.GetName().Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool ShouldSkipAssemblyForScan(string asmName)
    {
        // Fast skip: assemblies we KNOW cannot host CharSerialize-typed
        // properties. These include the BCL, Unity engine modules, the
        // Il2CppInterop runtime, and BepInEx itself.
        if (string.IsNullOrEmpty(asmName)) return false;
        if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("System", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Microsoft", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("BepInEx", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("MonoMod", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("HarmonyX", StringComparison.Ordinal) || asmName == "0Harmony") return true;
        if (asmName.StartsWith("mscorlib", StringComparison.Ordinal) || asmName.StartsWith("netstandard", StringComparison.Ordinal)) return true;
        return false;
    }

    private readonly struct StaticAccessor
    {
        public StaticAccessor(string name, Func<object?> read) { NamePretty = name; Read = read; }
        public string NamePretty { get; }
        public Func<object?> Read { get; }
    }

    private static bool TryResolveStaticInstanceAccessor(Type owner, out StaticAccessor accessor)
    {
        // Property style: `static T Instance { get; }` etc.
        foreach (var name in new[] { "Instance", "Current", "Singleton", "Default" })
        {
            var p = owner.GetProperty(name, AnyStatic);
            if (p is not null && p.GetMethod is not null && owner.IsAssignableFrom(p.PropertyType))
            {
                var capturedProp = p;
                accessor = new StaticAccessor($"{name} (property)", () => capturedProp.GetValue(null));
                return true;
            }
        }
        // Field style: `static T s_Instance` / `static T _instance`.
        foreach (var name in new[] { "_instance", "s_instance", "Instance", "s_Instance" })
        {
            var f = owner.GetField(name, AnyStatic);
            if (f is not null && owner.IsAssignableFrom(f.FieldType))
            {
                var capturedField = f;
                accessor = new StaticAccessor($"{name} (field)", () => capturedField.GetValue(null));
                return true;
            }
        }
        accessor = default;
        return false;
    }
}
