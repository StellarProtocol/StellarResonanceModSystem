using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Multi-candidate CharSerialize accessor resolution for
/// <see cref="PandaInventoryPullReader"/>. Earlier iterations resolved the FIRST
/// accessor that produced a non-null handle, but a Pure ECS component
/// (<c>CharDataComponent</c>) reached via <c>ZSingleton&lt;T&gt;.Instance</c>
/// yields a <em>default/empty</em> <c>CharSerialize</c> (ItemPackage present
/// but zero packages) — a non-null-but-empty trap. The live data lives behind
/// a different accessor (an <c>IPureComponentAccessor</c>, a container-resolved
/// <c>ICharDataObtain</c>, or the decoded server container-sync payload).
///
/// <para>This partial enumerates EVERY candidate in priority order and
/// selects the first whose CharSerialize carries actual data
/// (any ItemPackage package with at least one item). Each candidate's
/// outcome is logged under <c>STELLAR_DIAGNOSTICS=1</c> so the next
/// iteration can see which accessor won and why the others failed.</para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private sealed class CandidateAccessor
    {
        public CandidateAccessor(int index, string description, Func<object?> read)
        {
            Index = index;
            Description = description;
            Read = read;
        }
        public int Index { get; }
        public string Description { get; }
        public Func<object?> Read { get; }
    }

    // Build the ordered candidate list. Order matters — the data-aware selector
    // takes the FIRST candidate that yields a non-empty CharSerialize:
    //   0. captured ContainerSyncService payload (gated-off hook; leads when on)
    //   1. MainEntity → AsPureEntity → ZPureReader<CharDataComponent>.Read()
    //      — the PRIMARY pull-based path. Reaches the live Pure-ECS component
    //      via the proven ZSingleton<ZEntityMgr>.MainEntity accessor, with NO
    //      dependency on the VContainer (which never attaches on this build).
    //   2. IPureComponentAccessor<CharDataComponent>.Value._Data (container/static)
    //   3. ICharDataObtain.Data (container)
    //   4. ICharDataWatcherService.Data (container)
    //   5. *ContainerData*/*ContainerDirty* MessageEvent payloads (ZSingleton)
    //   6. broad scan — any static/singleton-reachable CharSerialize member
    private List<CandidateAccessor> BuildCandidates(Type charSerializeType)
    {
        var list = new List<CandidateAccessor>(capacity: 8);
        var next = 1;

        AddCapturedCandidate(list, ref next);
        AddPureReaderCandidate(list, charSerializeType, ref next);
        AddPureComponentAccessorCandidates(list, charSerializeType, ref next);
        AddContainerServiceCandidates(list, charSerializeType, ref next);
        AddSyncContainerEventCandidates(list, charSerializeType, ref next);
        AddBroadScanCandidates(list, charSerializeType, ref next);

        return list;
    }

    // ── Candidate (highest priority): HarmonyX-captured live CharSerialize ──
    // The WorldNtfStub.OnCallStub postfix decodes the method-21 SyncContainerData
    // full-sync payload into a CharSerialize and latches it. This is the only
    // path empirically confirmed to carry live data on this build, so it leads
    // the list.
    private void AddCapturedCandidate(List<CandidateAccessor> list, ref int next)
    {
        var idx = next++;
        list.Add(new CandidateAccessor(idx,
            "captured WorldNtf SyncContainerData (method 21 → CharSerialize)",
            ReadCapturedCharSerialize));
    }

    // Reads the latched CharSerialize captured from the WorldNtf method-21
    // full sync by the WireCapture collaborator. The decoded proto object is
    // long-lived; the candidate-0 accessor serves it directly off the shared
    // state.
    private object? ReadCapturedCharSerialize() => _state.CapturedCharSerialize;

    // ── Candidate 1: IPureComponentAccessor<CharDataComponent>.Value._Data ──
    // CharDataComponent is a Pure ECS struct; the LIVE instance comes from the
    // accessor's `.Value`, NOT from ZSingleton. Resolve the closed-generic
    // accessor from the VContainer first, then any static holder of it.
    private void AddPureComponentAccessorCandidates(List<CandidateAccessor> list, Type charSerializeType, ref int next)
    {
        var componentType = _typeRegistry.FindType("Panda.ZGame.CharDataComponent")
            ?? FindTypeByShortName("CharDataComponent");
        if (componentType is null) return;

        // The CharSerialize member ON the component (recon: `_Data` backing
        // field exposed as a `CharSerialize`-typed member).
        var memberReader = TryBuildInstanceCharSerializeMemberReader(componentType, charSerializeType);
        if (memberReader is null) return;

        var accessorIface = ResolvePureComponentAccessorType(componentType);
        if (accessorIface is null) return;

        var valueReader = BuildAccessorValueReader(accessorIface, componentType);
        if (valueReader is null) return;

        // 1a. Accessor resolved from the VContainer.
        AddAccessorViaContainer(list, accessorIface, valueReader, memberReader, ref next);

        // 1b. Accessor reached via any static/singleton holder (ECS-world).
        AddAccessorViaSingleton(list, accessorIface, valueReader, memberReader, ref next);
    }

    // Adds a candidate that resolves the IPureComponentAccessor via the VContainer.
    private void AddAccessorViaContainer(
        List<CandidateAccessor> list,
        Type accessorIface,
        Func<object, object?> valueReader,
        CharSerializeMemberReader memberReader,
        ref int next)
    {
        if (_objectResolver is null) return;
        var resolveMethod = FindContainerResolveMethod(_objectResolver.GetType());
        if (resolveMethod is null) return;

        var resolver = _objectResolver;
        var iface = accessorIface;
        var capturedValue = valueReader;
        var capturedMember = memberReader;
        var idx = next++;
        list.Add(new CandidateAccessor(idx,
            $"IPureComponentAccessor<CharDataComponent>.Value.{capturedMember.MemberName} (container)",
            () =>
            {
                object? accessor;
                try { accessor = resolveMethod.Invoke(resolver, new object[] { iface }); }
                catch { return null; }
                if (accessor is null) return null;
                var component = capturedValue(accessor);
                if (component is null) return null;
                return capturedMember.Read(component);
            }));
    }

    // Adds a candidate that reaches the IPureComponentAccessor via any
    // static/singleton holder in the ECS world.
    private void AddAccessorViaSingleton(
        List<CandidateAccessor> list,
        Type accessorIface,
        Func<object, object?> valueReader,
        CharSerializeMemberReader memberReader,
        ref int next)
    {
        var accessorProvider = ResolveAnyInstanceProvider(accessorIface);
        if (accessorProvider is null) return;

        var provider = accessorProvider;
        var capturedValue = valueReader;
        var capturedMember = memberReader;
        var idx = next++;
        list.Add(new CandidateAccessor(idx,
            $"IPureComponentAccessor<CharDataComponent>.Value.{capturedMember.MemberName} via {provider.NamePretty}",
            () =>
            {
                var accessor = provider.Read();
                if (accessor is null) return null;
                var component = capturedValue(accessor);
                if (component is null) return null;
                return capturedMember.Read(component);
            }));
    }

    // Finds the closed IPureComponentAccessor<CharDataComponent> interface
    // type. The interface is generic on the component type; the C# name is
    // `Panda.ZGame.Pure.IPureComponentAccessor`1`.
    private Type? ResolvePureComponentAccessorType(Type componentType)
    {
        Type? open = _typeRegistry.FindType("Panda.ZGame.Pure.IPureComponentAccessor`1")
            ?? FindOpenGenericByShortName("IPureComponentAccessor`1");
        if (open is null) return null;
        try { return open.MakeGenericType(componentType); }
        catch { return null; }
    }

    // Builds a reader that pulls the component value out of an
    // IPureComponentAccessor<T>. The recon shows `get_Value` returns a
    // by-ref CharDataComponent; reflection surfaces it as a `Value` property
    // (or a `GetValue`/`get_Value` method) whose return assignable-from the
    // component type. Boxing the by-ref struct yields a copy — fine for reads.
    private static Func<object, object?>? BuildAccessorValueReader(Type accessorType, Type componentType)
    {
        var prop = accessorType.GetProperty("Value", AnyInstance);
        if (prop is not null && IsComponentCompatible(prop.PropertyType, componentType))
        {
            var captured = prop;
            return owner => { try { return captured.GetValue(owner); } catch { return null; } };
        }

        foreach (var m in EnumerateMethods(accessorType))
        {
            if (m.Name != "get_Value" && m.Name != "GetValue") continue;
            if (m.GetParameters().Length != 0) continue;
            if (!IsComponentCompatible(m.ReturnType, componentType)) continue;
            var captured = m;
            return owner => { try { return captured.Invoke(owner, Array.Empty<object>()); } catch { return null; } };
        }
        return null;
    }

    private static bool IsComponentCompatible(Type declared, Type componentType)
    {
        // By-ref return surfaces as `T&`; unwrap before comparison.
        if (declared.IsByRef) declared = declared.GetElementType() ?? declared;
        return declared == componentType
            || componentType.IsAssignableFrom(declared)
            || declared.IsAssignableFrom(componentType);
    }

    // ── Candidates 2/3: ICharDataObtain.Data / ICharDataWatcherService.Data ──
    // Resolved from the VContainer. Each candidate re-resolves the service per
    // read so a late-bound registration is picked up. Detailed per-read failure
    // reasons are surfaced by the data-aware selector's logging.
    private void AddContainerServiceCandidates(List<CandidateAccessor> list, Type charSerializeType, ref int next)
    {
        var resolver = _objectResolver;
        if (resolver is null) return;
        var resolveMethod = FindContainerResolveMethod(resolver.GetType());
        if (resolveMethod is null) return;

        foreach (var typeName in new[] { "ICharDataObtain", "ICharDataWatcherService" })
        {
            var serviceType = _typeRegistry.FindType(typeName) ?? FindTypeByShortName(typeName);
            if (serviceType is null) continue;

            var captured = resolveMethod;
            var capturedResolver = resolver;
            var capturedService = serviceType;
            var cs = charSerializeType;
            var idx = next++;
            var desc = $"{serviceType.Name}.Data (container)";
            list.Add(new CandidateAccessor(idx, desc, () =>
            {
                object? service;
                try { service = captured.Invoke(capturedResolver, new object[] { capturedService }); }
                catch { return null; }
                if (service is null) return null;
                var reader = TryBuildCharSerializeDataReader(service.GetType(), cs);
                if (reader is null) return null;
                return reader.Read(service);
            }));
        }
    }

    // ── Candidate 4: server container-sync payloads via ZSingleton ──
    // `Zservice.WorldNtfEvents.SyncContainer{Data,DirtyData}MessageEvent._VData`
    // is the decoded server container-sync payload. The user's packet sniff
    // confirmed `SyncContainerDirtyData` messages arrive; when CharDataComponent
    // is empty, this payload may hold the real live CharSerialize.
    private void AddSyncContainerEventCandidates(List<CandidateAccessor> list, Type charSerializeType, ref int next)
    {
        foreach (var eventType in FindContainerSyncEventTypes())
        {
            var memberReader = TryBuildInstanceCharSerializeMemberReader(eventType, charSerializeType);
            if (memberReader is null) continue;

            var provider = ResolveAnyInstanceProvider(eventType);
            if (provider is null) continue;

            var capturedProvider = provider;
            var capturedReader = memberReader;
            var idx = next++;
            list.Add(new CandidateAccessor(idx,
                $"{eventType.FullName}.{capturedReader.MemberName} via {capturedProvider.NamePretty}",
                () =>
                {
                    var inst = capturedProvider.Read();
                    if (inst is null) return null;
                    return capturedReader.Read(inst);
                }));
        }
    }

    // Scans loaded game assemblies for types whose name signals a container
    // (data or dirty) sync message event carrying a CharSerialize payload.
    private IEnumerable<Type> FindContainerSyncEventTypes()
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
                string name;
                try { name = t.Name; } catch { continue; }
                if (!name.Contains("MessageEvent", StringComparison.Ordinal)) continue;
                var hasContainerData = name.Contains("ContainerData", StringComparison.Ordinal)
                    || name.Contains("ContainerDirty", StringComparison.Ordinal)
                    || name.Contains("SyncContainer", StringComparison.Ordinal);
                if (!hasContainerData) continue;
                yield return t;
            }
        }
    }

    // ── Candidate 5: broad scan ──
    // Any class anywhere with a CharSerialize-typed instance member reachable
    // from a static/singleton accessor. This is the catch-all; it may emit
    // several candidates (one per matching owner) and the selector picks the
    // first with real data.
    private void AddBroadScanCandidates(List<CandidateAccessor> list, Type charSerializeType, ref int next)
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
                bool isClass;
                try { isClass = t.IsClass; } catch { continue; }
                if (!isClass) continue;

                var memberReader = TryBuildInstanceCharSerializeMemberReader(t, charSerializeType);
                if (memberReader is null) continue;

                var provider = ResolveAnyInstanceProvider(t);
                if (provider is null) continue;

                var capturedProvider = provider;
                var capturedReader = memberReader;
                var ownerName = t.FullName ?? t.Name;
                var idx = next++;
                list.Add(new CandidateAccessor(idx,
                    $"{ownerName}.{capturedReader.MemberName} via {capturedProvider.NamePretty}",
                    () =>
                    {
                        var inst = capturedProvider.Read();
                        if (inst is null) return null;
                        return capturedReader.Read(inst);
                    }));
            }
        }
    }

    // ── Data-aware selection ──
    // Invokes each candidate in order, classifies its outcome, and returns the
    // first that yields a CharSerialize with real data (any package with >0
    // items). Returns null if no candidate has data yet (pre-character or
    // not-yet-synced) — the 1Hz poll re-runs the whole probe next tick.
    private Func<object?>? SelectBestCandidate(List<CandidateAccessor> candidates)
    {
        CandidateAccessor? best = null;
        foreach (var candidate in candidates)
        {
            var outcome = ProbeCandidate(candidate);
            OnCandidateProbed(candidate, outcome);
            if (outcome.HasData && best is null)
            {
                best = candidate;
                // Keep probing the rest only for the diagnostic log; once we
                // have a winner, short-circuit when diagnostics is off to save
                // the per-tick cost.
                if (!StellarDiagnostics.IsEnabled) break;
            }
        }

        if (best is null) return null;

        var winner = best;
        _charSerializeSource = winner.Description;
        OnCandidateSelected(winner);
        var read = winner.Read;
        return () => read();
    }

    private readonly struct CandidateOutcome
    {
        public CandidateOutcome(bool hasData, int packageCount, int modPackageItemCount, string summary)
        {
            HasData = hasData;
            PackageCount = packageCount;
            ModPackageItemCount = modPackageItemCount;
            Summary = summary;
        }
        public bool HasData { get; }
        public int PackageCount { get; }
        public int ModPackageItemCount { get; }
        public string Summary { get; }
    }

    // Invokes a candidate and classifies the result for the data-aware select.
    // "Has data" = at least one ItemPackage package with at least one item.
    private CandidateOutcome ProbeCandidate(CandidateAccessor candidate)
    {
        object? charSerialize;
        try { charSerialize = candidate.Read(); }
        catch (Exception ex) { return new CandidateOutcome(false, 0, 0, $"accessor threw {ex.GetType().Name}"); }
        if (charSerialize is null) return new CandidateOutcome(false, 0, 0, "CharSerialize null");

        if (_itemPackageProperty is null) return new CandidateOutcome(false, 0, 0, "ItemPackage property unresolved");
        object? itemPackage;
        try { itemPackage = _itemPackageProperty.GetValue(charSerialize); }
        catch (Exception ex) { return new CandidateOutcome(false, 0, 0, $"ItemPackage getter threw {ex.GetType().Name}"); }
        if (itemPackage is null) return new CandidateOutcome(false, 0, 0, "ItemPackage null");

        if (_packagesProperty is null) return new CandidateOutcome(false, 0, 0, "Packages property unresolved");
        object? packagesMap;
        try { packagesMap = _packagesProperty.GetValue(itemPackage); }
        catch (Exception ex) { return new CandidateOutcome(false, 0, 0, $"Packages getter threw {ex.GetType().Name}"); }
        if (packagesMap is null) return new CandidateOutcome(false, 0, 0, "Packages null");

        return ClassifyPackages(packagesMap);
    }

    // Walks the Packages map; "has data" if ANY package contains items.
    // Reports the total package count and the mod-package (key 5) item count.
    private CandidateOutcome ClassifyPackages(object packagesMap)
    {
        var packageCount = 0;
        var totalItems = 0;
        var modPackageItems = 0;
        foreach (var (key, package) in EnumerateMapEntries(packagesMap))
        {
            packageCount++;
            if (package is null) continue;
            var items = CountPackageItems(package);
            totalItems += items;
            if (AsInt32(key) == ModPackageKey) modPackageItems += items;
        }

        if (packageCount == 0) return new CandidateOutcome(false, 0, 0, "empty (0 packages)");
        if (totalItems == 0) return new CandidateOutcome(false, packageCount, 0, $"empty ({packageCount} packages, 0 items)");
        return new CandidateOutcome(true, packageCount, modPackageItems,
            $"OK — {packageCount} packages, mod-pkg(5)={modPackageItems} items, {totalItems} total items");
    }

    private int CountPackageItems(object package)
    {
        var itemsProp = FindMapLikeProperty(package.GetType(), "Items");
        if (itemsProp is null) return 0;
        object? itemsMap;
        try { itemsMap = itemsProp.GetValue(package); }
        catch { return 0; }
        if (itemsMap is null) return 0;
        var count = 0;
        foreach (var _ in EnumerateMapValues(itemsMap)) count++;
        return count;
    }

    // Helpers reused across candidate builders.

    private static IEnumerable<MethodInfo> EnumerateMethods(Type t)
    {
        MethodInfo[] methods;
        try { methods = t.GetMethods(AnyInstance); } catch { yield break; }
        foreach (var m in methods) yield return m;
    }

    private static Type? FindOpenGenericByShortName(string shortName)
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
                string name;
                try { name = t.Name; } catch { continue; }
                if (string.Equals(name, shortName, StringComparison.Ordinal)) return t;
            }
        }
        return null;
    }
}
