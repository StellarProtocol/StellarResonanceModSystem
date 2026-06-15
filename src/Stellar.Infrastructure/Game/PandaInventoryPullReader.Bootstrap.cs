using System;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-resolution machinery for <see cref="PandaInventoryPullReader"/>.
/// Lazily walks the loaded assemblies on the first <c>TryRead*</c> call to
/// resolve the <c>CharSerialize</c> type, its sub-archive properties
/// (Mod, ItemPackage, ModSlots, ModInfos, Packages, Items, ModParts,
/// InitLinkNums, Uuid, ConfigId, Quality), the <c>ModTableBase</c> type
/// (for <c>ModType</c>-derived category resolution), and a live
/// <c>CharSerialize</c> accessor (a static singleton on whatever class
/// holds the player's current data — typically a <c>CharDataComponent</c>
/// or <c>ICharDataObtain</c> service).
///
/// <para>Resolution is one-shot: once <c>_resolutionSucceeded</c> flips
/// true, all subsequent reads hit the cached handles directly.</para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private bool EnsureResolved()
    {
        if (_resolutionSucceeded)
        {
            return true;
        }
        _failedResolutionAttempts++;

        // Backoff: after a few quick tries, stop running the expensive
        // AppDomain-wide scan every poll (it froze the game ~0.5s/sec). The
        // capture postfix resets _failedResolutionAttempts to 0 when real data
        // lands, so the very next poll retries immediately and resolves.
        if (_failedResolutionAttempts > ResolutionFastAttempts
            && (_failedResolutionAttempts % ResolutionBackoffEvery) != 0)
        {
            return false;
        }

        // Top-level guard: reflection-driven type discovery can throw
        // TypeLoadException on Il2CppInterop-generated assemblies that
        // reference unavailable generic constraints (e.g. UnityEngine.Android.*
        // types on Linux). Swallow the failure here so the 1Hz poll backs off
        // gracefully instead of spamming the wrap-catch in BootstrapPlugin.
        try
        {
            return TryResolveAll();
        }
        catch (Exception ex)
        {
            OnResolutionFailureLogged($"resolution threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveAll()
    {
        // Walk the loaded assemblies for the CharSerialize type itself and a
        // static-accessible "current player's" instance. The instance is
        // typically reached via an ECS component (`CharDataComponent.Data`) or
        // a service (`ICharDataObtain.Data`). We try the simpler accessors
        // first and fall back to component-walk only if no direct singleton
        // exposes the data.
        var charSerializeType = _typeRegistry.FindType("Zproto.CharSerialize")
            ?? FindTypeByShortName("CharSerialize");
        if (charSerializeType is null)
        {
            OnResolutionFailureLogged("Zproto.CharSerialize type not loaded yet");
            return false;
        }

        if (!ResolveCharSerializeProperties(charSerializeType)) return false;
        ResolveSubProperties();

        // Resolve a way to fetch the LIVE CharSerialize instance. There's no
        // universal pattern — try a sequence of recon-known candidates.
        _readCharSerialize = ResolveCharSerializeReader(charSerializeType);
        if (_readCharSerialize is null)
        {
            OnResolutionFailureLogged($"could not resolve live CharSerialize accessor (CharSerialize type={charSerializeType.FullName})");
            return false;
        }

        _resolutionSucceeded = true;
        OnResolutionSucceededLogged(charSerializeType);
        return true;
    }

    // Resolves the top-level CharSerialize.Mod and CharSerialize.ItemPackage
    // properties, then the map-like children hanging off each. Returns false
    // (and logs) if the required top-level properties are absent.
    private bool ResolveCharSerializeProperties(Type charSerializeType)
    {
        _modProperty = charSerializeType.GetProperty("Mod", AnyInstance);
        _itemPackageProperty = charSerializeType.GetProperty("ItemPackage", AnyInstance);
        if (_modProperty is null || _itemPackageProperty is null)
        {
            OnResolutionFailureLogged($"CharSerialize.Mod ({_modProperty is not null}) / ItemPackage ({_itemPackageProperty is not null}) not found");
            return false;
        }

        // Walk further: Mod → ModSlots + ModInfos, ItemPackage → Packages.
        // The Package value type is on the MapField's V argument. We can't
        // reflect statically (proto MapField uses generic open types via codec),
        // so resolve from the actual runtime instance on first iteration.
        var modType = _modProperty.PropertyType;
        _modSlotsProperty = FindMapLikeProperty(modType, "ModSlots");
        _modInfosProperty = FindMapLikeProperty(modType, "ModInfos");

        var itemPackageType = _itemPackageProperty.PropertyType;
        _packagesProperty = FindMapLikeProperty(itemPackageType, "Packages");

        // Resonance (Battle Imagine) — field 28 → zproto.Resonance with
        // Installed (repeated uint32, equipped ids in slot order). OPTIONAL:
        // a missing Resonance property must NOT fail inventory resolution.
        _resonanceProperty = charSerializeType.GetProperty("Resonance", AnyInstance);
        if (_resonanceProperty is not null)
        {
            _resonanceInstalledProperty = FindMapLikeProperty(_resonanceProperty.PropertyType, "Installed");
        }
        return true;
    }

    // Resolves item/mod sub-type property handles (Item, ModNewAttr, ModInfo,
    // ModTableBase) using the type registry. All are optional — missing handles
    // degrade individual read paths gracefully.
    private void ResolveSubProperties()
    {
        var itemType = _typeRegistry.FindType("Zproto.Item") ?? FindTypeByShortName("Item");
        if (itemType is not null)
        {
            _itemUuidProperty = itemType.GetProperty("Uuid", AnyInstance);
            _itemConfigIdProperty = itemType.GetProperty("ConfigId", AnyInstance);
            _itemQualityProperty = itemType.GetProperty("Quality", AnyInstance);
            _itemModNewAttrProperty = itemType.GetProperty("ModNewAttr", AnyInstance);
        }

        var modNewAttrType = _typeRegistry.FindType("Zproto.ModNewAttr") ?? FindTypeByShortName("ModNewAttr");
        if (modNewAttrType is not null)
        {
            _modNewAttrPartsProperty = modNewAttrType.GetProperty("ModParts", AnyInstance);
        }

        var modInfoType = _typeRegistry.FindType("Zproto.ModInfo") ?? FindTypeByShortName("ModInfo");
        if (modInfoType is not null)
        {
            _modInfoInitLinkNumsProperty = modInfoType.GetProperty("InitLinkNums", AnyInstance);
        }

        // ModTableBase for category resolution.
        _modTableBaseType = _typeRegistry.FindType("Bokura.ModTableBase") ?? FindTypeByShortName("ModTableBase");
        if (_modTableBaseType is not null)
        {
            _modTableGetTableMethod = _modTableBaseType.GetMethod("GetTable", AnyStatic);
            _modTypeProperty = _modTableBaseType.GetProperty("ModType", AnyInstance);
        }
    }

    // Lazily resolve just the ItemPackage → Packages handles needed to
    // classify a captured CharSerialize's data-richness. Used by the capture
    // diagnostic, which can fire before the full TryResolveAll has run (the
    // postfix runs on the network thread, ahead of the 1Hz game-thread poll).
    internal void EnsureShapeHandles(Type charSerializeType)
    {
        _itemPackageProperty ??= charSerializeType.GetProperty("ItemPackage", AnyInstance);
        if (_itemPackageProperty is null) return;
        if (_packagesProperty is null)
        {
            _packagesProperty = FindMapLikeProperty(_itemPackageProperty.PropertyType, "Packages");
        }
    }

    // Lazily resolve just the Mod → ModSlots handles needed to read the equipped
    // set. Used by the full-sync reseed (ReseedEquippedFromSync), which runs in
    // the network-thread postfix and may fire before the 1Hz game-thread
    // TryResolveAll has resolved these handles. Idempotent and additive — never
    // clobbers handles the full resolver already set.
    internal void EnsureModSlotHandles(Type charSerializeType)
    {
        _modProperty ??= charSerializeType.GetProperty("Mod", AnyInstance);
        if (_modProperty is null) return;
        if (_modSlotsProperty is null)
        {
            _modSlotsProperty = FindMapLikeProperty(_modProperty.PropertyType, "ModSlots");
        }
    }

    internal static PropertyInfo? FindMapLikeProperty(Type host, string baseName)
    {
        // Proto-generated MapField properties use the simple name; some
        // generators add the `__Value` suffix on the underlying value-accessor.
        // Both shapes have been observed in the recon dump.
        return host.GetProperty(baseName, AnyInstance)
            ?? host.GetProperty(baseName + "__Value", AnyInstance);
    }

    internal static Type? FindTypeByShortName(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = SafeAssemblyName(asm);
            if (ShouldSkipAssemblyForScan(asmName)) continue;

            // GetTypes() can throw TypeLoadException (not just ReflectionTypeLoad)
            // on Il2CppInterop-generated assemblies that reference unavailable
            // generic constraints (e.g. UnityEngine.Android.* types on Linux).
            // Swallow all type-load failures and skip that assembly.
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                string name;
                try { name = t.Name; } catch { continue; }
                if (string.Equals(name, shortName, StringComparison.Ordinal))
                {
                    return t;
                }
            }
        }
        return null;
    }

    // Resolves a delegate that returns the live CharSerialize on each call.
    //
    // Strategy: build ALL candidate accessors (PandaInventoryPullReader.Candidates.cs)
    // in priority order, then INVOKE each and select the first that yields a
    // CharSerialize with real data (any ItemPackage package with >0 items).
    //
    // The data-aware selection is the crux: a Pure ECS component
    // (CharDataComponent) reached via ZSingleton<T>.Instance returns a
    // non-null-but-EMPTY CharSerialize (the trap that broke iteration 3). The
    // selector probes data-richness, not mere non-null, so it skips the empty
    // singleton and locks onto the accessor that actually carries the player's
    // inventory (an IPureComponentAccessor, a container-resolved service, or
    // the decoded server container-sync payload).
    //
    // Returns null when NO candidate has data yet (pre-character / not synced);
    // resolution stays unsuccessful so the 1Hz poll re-runs the probe until a
    // candidate produces live data.
    private Func<object?>? ResolveCharSerializeReader(Type charSerializeType)
    {
        // CAPTURE-ONLY fast path. When the SyncContainerData postfix is
        // installed, the live CharSerialize comes straight off the latched
        // capture field — so the resolver serves ONLY that reader and NEVER
        // runs the pull-based candidates (PureReader, container, sync-event)
        // or the broad AppDomain GetTypes() scan. Those scans cost ~0.5s and,
        // run on the 1Hz poll during scene streaming, froze world-load. The
        // capture candidate is cheap to probe (a couple of GetProperty getters
        // in ProbeCandidate) and returns null until the first sync lands, so
        // pre-capture reads abort cheaply and post-capture reads serve live
        // data — zero broad scan either way.
        if (_state.CaptureHookActive)
        {
            return ResolveCapturedOnlyReader();
        }

        // Cache the built candidate list across attempts. BuildCandidates runs
        // several AppDomain-wide type scans (broad scan, sync-event scan,
        // GetReader discovery) costing ~0.5s — re-running that on each 1Hz
        // re-attempt put a frame hitch on the world-load critical path. The
        // candidate DELEGATES re-resolve live state (MainEntity, accessors) on
        // every invocation, so the list itself is stable once built; only the
        // cheap SelectBestCandidate (which invokes each delegate) needs to re-run.
        // Rebuild only when the cache is empty OR the VContainer resolver attached
        // after the cache was built (which would add the container candidates).
        if (_cachedCandidates is null
            || _cachedCandidates.Count == 0
            || (_objectResolver is not null && !_candidatesBuiltWithResolver))
        {
            _cachedCandidates = BuildCandidates(charSerializeType);
            _candidatesBuiltWithResolver = _objectResolver is not null;
            OnCandidatesBuilt(_cachedCandidates.Count);
        }
        return SelectBestCandidate(_cachedCandidates);
    }

    // Builds (once) a single-candidate list holding only the latched-capture
    // reader and runs the data-aware selector over it. No BuildCandidates call,
    // so the broad AppDomain scan + PureReader/container reflection never run
    // while the hook is active. Returns null until the first SyncContainerData
    // is latched (selector finds no data yet) — the cheap CharSerializeNull
    // abort path on the next poll, no scan.
    private Func<object?>? ResolveCapturedOnlyReader()
    {
        if (_cachedCandidates is null || _cachedCandidates.Count != 1)
        {
            var captureOnly = new List<CandidateAccessor>(capacity: 1);
            var next = 0;
            AddCapturedCandidate(captureOnly, ref next);
            _cachedCandidates = captureOnly;
            _candidatesBuiltWithResolver = false;
            OnCandidatesBuilt(_cachedCandidates.Count);
        }
        return SelectBestCandidate(_cachedCandidates);
    }

    private List<CandidateAccessor>? _cachedCandidates;
    private bool _candidatesBuiltWithResolver;

    // Drops any pull-based candidate list built before the capture hook
    // registered, so the next resolution attempt rebuilds via the capture-only
    // fast path. Called by the WireCapture collaborator from RegisterWith.
    internal void ResetCandidateCache() => _cachedCandidates = null;

    internal sealed class CharSerializeMemberReader
    {
        public CharSerializeMemberReader(string memberName, Func<object, object?> read)
        {
            MemberName = memberName;
            Read = read;
        }
        public string MemberName { get; }
        public Func<object, object?> Read { get; }
    }

    internal static CharSerializeMemberReader? TryBuildInstanceCharSerializeMemberReader(Type t, Type charSerializeType)
    {
        // Properties first (preferred — properties are more stable than backing fields).
        PropertyInfo[] props;
        try { props = t.GetProperties(AnyInstance); } catch { props = Array.Empty<PropertyInfo>(); }
        foreach (var p in props)
        {
            Type ptype;
            try { ptype = p.PropertyType; } catch { continue; }
            if (ptype != charSerializeType) continue;
            var captured = p;
            return new CharSerializeMemberReader(
                $"{captured.Name} (property)",
                owner =>
                {
                    try { return captured.GetValue(owner); }
                    catch { return null; }
                });
        }
        // Then private fields (e.g. _cachedCharSerialize per the recon).
        FieldInfo[] fields;
        try { fields = t.GetFields(AnyInstance); } catch { fields = Array.Empty<FieldInfo>(); }
        foreach (var f in fields)
        {
            Type ftype;
            try { ftype = f.FieldType; } catch { continue; }
            if (ftype != charSerializeType) continue;
            var captured = f;
            return new CharSerializeMemberReader(
                $"{captured.Name} (field)",
                owner =>
                {
                    try { return captured.GetValue(owner); }
                    catch { return null; }
                });
        }
        return null;
    }

}
