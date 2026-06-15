using System;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure-ECS read candidate for <see cref="PandaInventoryPullReader"/> that reaches
/// the live <c>CharDataComponent</c> WITHOUT the VContainer
/// <c>IObjectResolver</c> (which never attaches off <c>Game.GameRoot</c> on
/// this build) and WITHOUT the boot-crashing capture hook.
///
/// <para>The chain mirrors <c>PandaPlayerStateProbe</c>'s proven path to the
/// local player and extends it through the Panda Pure-ECS reader API
/// (recon §1):</para>
/// <list type="number">
///   <item><c>ZUtil.ZSingleton&lt;Panda.ZGame.ZEntityMgr&gt;.Instance.MainEntity</c>
///         → the local player's <c>ZEntity</c>.</item>
///   <item><c>ZEntity.AsPureEntity()</c> → <c>ZPureEntity</c>.</item>
///   <item>the static generic <c>GetReader&lt;CharDataComponent&gt;(ZPureEntity)</c>
///         → <c>ZPureReader&lt;CharDataComponent&gt;</c> (the same accessor the
///         game uses to read Pure components — equivalent to the recon's
///         <c>IPureComponentAccessor&lt;CharDataComponent&gt;.Value</c>).</item>
///   <item><c>ZPureReader&lt;T&gt;.Read()</c> → the live <c>CharDataComponent</c>
///         struct (boxed — a read copy, sufficient for inventory inspection).</item>
///   <item>the component's <c>CharSerialize</c> member, read either directly or
///         via a <c>CharSerializeContainerArchive.Data</c> hop
///         (<see cref="ResolveComponentCharSerializeReader"/>).</item>
/// </list>
///
/// <para>This is the highest-priority PULL-based candidate: it sits just below
/// the (gated-off) capture candidate and above the container-resolved
/// <c>IPureComponentAccessor</c> / <c>ICharDataObtain</c> candidates, which
/// depend on the VContainer that never attaches.</para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private const string ZEntityMgrTypeName = "Panda.ZGame.ZEntityMgr";

    // ── Candidate: MainEntity → AsPureEntity → ZPureReader<CharDataComponent> ──
    private void AddPureReaderCandidate(List<CandidateAccessor> list, Type charSerializeType, ref int next)
    {
        var componentType = _typeRegistry.FindType("Panda.ZGame.CharDataComponent")
            ?? FindTypeByShortName("CharDataComponent");
        if (componentType is null) return;

        // How to pull a CharSerialize out of a (boxed) CharDataComponent value —
        // direct member or a ContainerArchive.Data hop.
        var componentReader = ResolveComponentCharSerializeReader(componentType, charSerializeType);
        if (componentReader is null) return;

        // The MainEntity accessor (ZSingleton<ZEntityMgr>.Instance.MainEntity).
        var entityProvider = ResolveMainEntityProvider();
        if (entityProvider is null) return;

        // The static GetReader<CharDataComponent>(ZPureEntity) → ZPureReader<…>
        // and the reader's Read()/TryRead() value accessor, both closed once.
        var pureReaderInvoke = BuildPureReaderInvoke(componentType);
        if (pureReaderInvoke is null) return;

        var entity = entityProvider;
        var toReader = pureReaderInvoke;
        var toCharSerialize = componentReader;
        var idx = next++;
        list.Add(new CandidateAccessor(idx,
            "MainEntity.AsPureEntity → ZPureReader<CharDataComponent>.Read()",
            () =>
            {
                var pureEntity = entity();
                if (pureEntity is null) { OnPureReaderHop("MainEntity/AsPureEntity returned null"); return null; }
                var component = toReader(pureEntity);
                if (component is null) { OnPureReaderHop($"GetReader/Read returned null (pureEntity={pureEntity.GetType().Name})"); return null; }
                var cs = toCharSerialize(component);
                if (cs is null) { OnPureReaderHop($"component→CharSerialize returned null (component={component.GetType().Name})"); return null; }
                OnPureReaderHop($"OK CharSerialize via component={component.GetType().Name}");
                return cs;
            }));
    }

    // ZSingleton<ZEntityMgr>.Instance.MainEntity, then ZEntity.AsPureEntity().
    // Re-resolves the entity every call so a scene change (new local entity)
    // is picked up; the reflection handles themselves are cached.
    private Func<object?>? ResolveMainEntityProvider()
    {
        var mgrType = _typeRegistry.FindType(ZEntityMgrTypeName) ?? FindTypeByShortName("ZEntityMgr");
        if (mgrType is null) return null;

        var mgrInstance = ResolveZSingletonInstance(mgrType);
        if (mgrInstance is null) return null;

        var mainEntityProp = mgrType.GetProperty("MainEntity", AnyInstance)
            ?? mgrType.GetProperty("MainEnt", AnyInstance)
            ?? mgrType.GetProperty("PlayerEntity", AnyInstance);
        if (mainEntityProp is null) return null;

        var mgr = mgrInstance;
        var entityProp = mainEntityProp;
        MethodInfo? asPureCached = null;
        var asPureResolved = false;

        return () =>
        {
            object? mgrObj;
            try { mgrObj = mgr.Read(); }
            catch { return null; }
            if (mgrObj is null) return null;

            object? entity;
            try { entity = entityProp.GetValue(mgrObj); }
            catch { return null; }
            if (entity is null) return null;

            if (!asPureResolved)
            {
                asPureResolved = true;
                try
                {
                    asPureCached = entity.GetType().GetMethod(
                        "AsPureEntity", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
                }
                catch { asPureCached = null; }
            }
            if (asPureCached is null) return entity; // already a pure-capable entity?

            try { return asPureCached.Invoke(entity, Array.Empty<object>()); }
            catch { return null; }
        };
    }

    // Resolves the static generic GetReader<T>(ZPureEntity) → ZPureReader<T> and
    // the reader's value accessor, closes both on CharDataComponent, and returns
    // a delegate that maps a ZPureEntity → boxed CharDataComponent (or null).
    private Func<object, object?>? BuildPureReaderInvoke(Type componentType)
    {
        var pureEntityType = _typeRegistry.FindType("Panda.ZGame.Pure.ZPureEntity")
            ?? FindTypeByShortName("ZPureEntity");
        if (pureEntityType is null) return null;

        var getReaderOpen = FindStaticGetReaderMethod(pureEntityType);
        if (getReaderOpen is null) return null;

        MethodInfo getReaderClosed;
        try { getReaderClosed = getReaderOpen.MakeGenericMethod(componentType); }
        catch { return null; }

        var readerType = getReaderClosed.ReturnType;
        var readValue = BuildReaderReadDelegate(readerType, componentType);
        if (readValue is null) return null;

        var getReader = getReaderClosed;
        var read = readValue;
        return pureEntity =>
        {
            object? reader;
            try { reader = getReader.Invoke(null, new[] { pureEntity }); }
            catch { return null; }
            if (reader is null) return null;
            return read(reader);
        };
    }

    // Scans loaded game assemblies for the static generic method
    // `GetReader<T>(ZPureEntity) : ZPureReader<T>`. The declaring class is a
    // Pure-ECS extension/utility whose name we don't pin (recon only surfaced
    // the method signature), so we match by shape: one generic arg, one
    // ZPureEntity-typed parameter, generic return.
    private MethodInfo? FindStaticGetReaderMethod(Type pureEntityType)
    {
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
                MethodInfo[] methods;
                try { methods = t.GetMethods(AnyStatic); }
                catch { continue; }
                foreach (var m in methods)
                {
                    if (m.Name != "GetReader") continue;
                    if (!m.IsGenericMethodDefinition) continue;
                    if (m.GetGenericArguments().Length != 1) continue;
                    ParameterInfo[] ps;
                    try { ps = m.GetParameters(); }
                    catch { continue; }
                    if (ps.Length != 1) continue;
                    Type p0;
                    try { p0 = ps[0].ParameterType; } catch { continue; }
                    if (!IsPureEntityCompatible(p0, pureEntityType)) continue;
                    return m;
                }
            }
        }
        return null;
    }

    private static bool IsPureEntityCompatible(Type declared, Type pureEntityType)
    {
        if (declared.IsByRef) declared = declared.GetElementType() ?? declared;
        return declared == pureEntityType
            || pureEntityType.IsAssignableFrom(declared)
            || declared.IsAssignableFrom(pureEntityType);
    }

    // ZPureReader<T> exposes `T Read()` (recon: Read_Public_ZPureReader_1_T) and
    // `bool TryRead(out T)`. Prefer Read() (simplest); fall back to a Value
    // property or TryRead. Returns a delegate reader → boxed component value.
    private static Func<object, object?>? BuildReaderReadDelegate(Type readerType, Type componentType)
    {
        var readMethod = readerType.GetMethod("Read", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (readMethod is not null && IsComponentCompatible(readMethod.ReturnType, componentType))
        {
            var captured = readMethod;
            return reader => { try { return captured.Invoke(reader, Array.Empty<object>()); } catch { return null; } };
        }

        var valueProp = readerType.GetProperty("Value", AnyInstance);
        if (valueProp is not null && IsComponentCompatible(valueProp.PropertyType, componentType))
        {
            var captured = valueProp;
            return reader => { try { return captured.GetValue(reader); } catch { return null; } };
        }

        var tryRead = readerType.GetMethod("TryRead", AnyInstance);
        if (tryRead is not null)
        {
            var ps = tryRead.GetParameters();
            if (ps.Length == 1 && ps[0].IsOut)
            {
                var captured = tryRead;
                return reader =>
                {
                    try
                    {
                        var args = new object?[] { null };
                        var ok = captured.Invoke(reader, args);
                        if (ok is bool b && !b) return null;
                        return args[0];
                    }
                    catch { return null; }
                };
            }
        }
        return null;
    }

    // Resolves how to read a CharSerialize out of a (boxed) CharDataComponent.
    // The component may hold the CharSerialize directly, OR a
    // CharSerializeContainerArchive whose `Data`/`get_Data` returns CharSerialize
    // (recon §1: get_Data_Public_get_CharSerialize on the archive). We resolve
    // the path once and return a reader delegate.
    private Func<object, object?>? ResolveComponentCharSerializeReader(Type componentType, Type charSerializeType)
    {
        // 1. Direct CharSerialize-typed member on the component.
        var direct = TryBuildInstanceCharSerializeMemberReader(componentType, charSerializeType);
        if (direct is not null)
        {
            var captured = direct;
            return component => captured.Read(component);
        }

        // 2. Component holds a container/archive whose Data getter returns
        //    CharSerialize. Find the member, then the archive's Data accessor.
        var archiveHop = TryBuildArchiveHopReader(componentType, charSerializeType);
        if (archiveHop is not null) return archiveHop;

        return null;
    }

    // Finds a member on the component whose type exposes a CharSerialize-typed
    // `Data` (property or getter). Returns a two-hop reader.
    private Func<object, object?>? TryBuildArchiveHopReader(Type componentType, Type charSerializeType)
    {
        PropertyInfo[] props;
        try { props = componentType.GetProperties(AnyInstance); } catch { props = Array.Empty<PropertyInfo>(); }
        foreach (var p in props)
        {
            var reader = TryBuildArchiveHopFor(p.PropertyType, charSerializeType,
                owner => { try { return p.GetValue(owner); } catch { return null; } });
            if (reader is not null) return reader;
        }

        FieldInfo[] fields;
        try { fields = componentType.GetFields(AnyInstance); } catch { fields = Array.Empty<FieldInfo>(); }
        foreach (var f in fields)
        {
            var reader = TryBuildArchiveHopFor(f.FieldType, charSerializeType,
                owner => { try { return f.GetValue(owner); } catch { return null; } });
            if (reader is not null) return reader;
        }
        return null;
    }

    private static Func<object, object?>? TryBuildArchiveHopFor(
        Type memberType, Type charSerializeType, Func<object, object?> readMember)
    {
        // The member must itself expose a CharSerialize-typed Data accessor.
        PropertyInfo? dataProp;
        try { dataProp = memberType.GetProperty("Data", AnyInstance); }
        catch { dataProp = null; }
        if (dataProp is null || !IsCharSerializeCompatible(dataProp.PropertyType, charSerializeType))
        {
            return null;
        }

        var dataGetter = dataProp;
        return owner =>
        {
            var archive = readMember(owner);
            if (archive is null) return null;
            try { return dataGetter.GetValue(archive); }
            catch { return null; }
        };
    }
}
