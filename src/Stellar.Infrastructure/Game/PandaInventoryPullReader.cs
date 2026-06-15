using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection pull-read concern of the inventory probe (C-14 collaborator).
/// Walks the live <c>CharSerialize</c> instance held by a Panda ECS component
/// (<c>CharDataComponent</c> / <c>ICharDataObtain</c>) to surface:
/// <list type="bullet">
///   <item>The full module-package inventory
///         (<c>CharSerialize.ItemPackage.Packages</c> at key
///         <c>5</c>, filtered to items whose <c>ModNewAttr.ModParts</c> is
///         non-empty).</item>
///   <item>The currently equipped set
///         (<c>CharSerialize.Mod.ModSlots</c>, a
///         <c>MapField&lt;int, long&gt;</c> of slot → uuid).</item>
/// </list>
///
/// <para>
/// Resolution is lazy and one-shot. The reader explores the loaded
/// assemblies for the CharSerialize host on the first <c>TryRead*</c>
/// call, then caches every reflection handle. Pre-resolution calls
/// return <c>false</c>; post-resolution calls perform a single property
/// walk.
/// </para>
///
/// <para>
/// SOLID partial layout — bootstrap / reflection lives in
/// <c>PandaInventoryPullReader.Bootstrap.cs</c>; read logic in
/// <c>PandaInventoryPullReader.Read.cs</c>; the Il2CppInterop map-walking
/// helpers in <c>PandaInventoryPullReader.MapWalker.cs</c>; and diagnostic
/// logging in <c>PandaInventoryPullReader.Diagnostics.cs</c>, gated on
/// <see cref="StellarDiagnostics.IsEnabled"/> for repeating events.
/// </para>
///
/// <para>
/// The integer key for the module package is <c>5</c> (the Lua-level
/// <c>BackPackItemPackageType.Mod</c>; the proto-name skew renames the
/// C# enum to <c>PackageSign</c>, but the integer is canonical).
/// </para>
///
/// <para>
/// Shares the cross-thread mutable state (<see cref="InventoryProbeState"/>)
/// with the <see cref="PandaInventoryWireCapture"/> capture concern. The
/// dependency direction is one-way: WireCapture references this reader (for the
/// reseed fallback and the shared reflection helpers); this reader never
/// references WireCapture or the façade.
/// </para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // Module storage key — the integer enum value for `BackPackItemPackageType.Mod`
    // (Lua) / `EItemPackageType.PackageSign` (proto-generated C#). See recon §6.
    internal const int ModPackageKey = 5;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Cross-thread mutable state shared with the WorldNtf stub-capture concern
    // (equipped snapshot + captured CharSerialize latch + capture-hook flag).
    // The SAME instance is handed to both collaborators by the façade.
    private readonly InventoryProbeState _state;

    // Resolution / bootstrap state.
    private bool _resolutionSucceeded;
    private bool _resolutionFailureLogged;
    private int _failedResolutionAttempts;
    private const int ResolutionFailureLogEvery = 60; // every minute when polled at 1Hz

    // Resolution backoff. TryResolveAll's ResolveCharSerializeReader includes an
    // AppDomain-wide type scan (the broad fallback candidate) that costs ~0.5s.
    // Running it on every 1Hz poll froze the game once per second when resolution
    // can't succeed (capture hook disabled, or pre-login before the first
    // container sync). After ResolutionFastAttempts quick tries, only re-attempt
    // every ResolutionBackoffEvery calls. The capture postfix resets the counter
    // (OnCharSerializeCaptured) so resolution retries immediately once real data
    // arrives.
    // After a lifecycle reset (login / scene enter, OnLifecycleAdvanced) the
    // probe gets this many 1Hz fast attempts before backing off. Wide enough
    // (~15s) that the MainEntity + the first CharData sync both land inside the
    // window — the boot-time fast tries fire pre-character (MainEntity null), so
    // the reset is what actually drives the in-world resolve.
    private const int ResolutionFastAttempts = 15;
    // Once backed off, re-probe every ~30s at 1Hz. Slow enough that a failing
    // resolve (still pre-character) doesn't churn the AppDomain scan every
    // second (the ~0.5s hitch that froze the game), fast enough to recover if a
    // resolve is missed.
    private const int ResolutionBackoffEvery = 30;

    // Live CharSerialize accessor — wired by Bootstrap.ResolveCharSerializeReader.
    private Func<object?>? _readCharSerialize;
    private string _charSerializeSource = "(unresolved)";

    // VContainer IObjectResolver, attached by Host once the game root is probed.
    // The PREFERRED CharSerialize source: resolve ICharDataObtain /
    // ICharDataWatcherService from the container and read its live `.Data`
    // (recon §1). CharDataComponent is a Pure ECS struct, so the
    // ZSingleton<CharDataComponent>.Instance fallback yields a default/empty
    // CharSerialize — the container service is the canonical live accessor.
    private object? _objectResolver;

    // Property handles on CharSerialize and its sub-archives.
    private PropertyInfo? _modProperty;             // CharSerialize.Mod → ModContainerArchive
    private PropertyInfo? _itemPackageProperty;     // CharSerialize.ItemPackage → ItemPackageContainerArchive
    private PropertyInfo? _modSlotsProperty;        // ModContainerArchive.ModSlots → MapField<int, long>
    private PropertyInfo? _modInfosProperty;        // ModContainerArchive.ModInfos → MapField<long, ModInfoContainerArchive>
    private PropertyInfo? _packagesProperty;        // ItemPackageContainerArchive.Packages → MapField<int, PackageContainerArchive>
    private PropertyInfo? _packageItemsProperty;    // PackageContainerArchive.Items → MapField<long, ItemContainerArchive>
    private PropertyInfo? _itemUuidProperty;        // ItemContainerArchive.Uuid → long
    private PropertyInfo? _itemConfigIdProperty;    // ItemContainerArchive.ConfigId → int
    private PropertyInfo? _itemQualityProperty;     // ItemContainerArchive.Quality → int
    private PropertyInfo? _itemModNewAttrProperty;  // ItemContainerArchive.ModNewAttr → ModNewAttrContainerArchive
    private PropertyInfo? _modNewAttrPartsProperty; // ModNewAttrContainerArchive.ModParts → RepeatedField<int>
    private PropertyInfo? _modInfoInitLinkNumsProperty; // ModInfoContainerArchive.InitLinkNums → RepeatedField<int>

    // Resonance (Battle Imagine) — CharSerialize.Resonance (proto field 28) →
    // zproto.Resonance.Installed (repeated uint32, equipped ids in slot order).
    private PropertyInfo? _resonanceProperty;          // CharSerialize.Resonance → Resonance
    private PropertyInfo? _resonanceInstalledProperty; // Resonance.Installed → RepeatedField<uint>

    // ModTableBase.ModType for category resolution.
    private Type? _modTableBaseType;
    private MethodInfo? _modTableGetTableMethod; // static GetTable(bool) → ZTable<int, ModTableBase>
    private PropertyInfo? _modTypeProperty;      // ModTableBase.ModType → int
    private object? _cachedModTable;             // cached return of GetTable(true)
    private MethodInfo? _modTableContainsKey;
    private MethodInfo? _modTableGetItem;
    private readonly Dictionary<int, ModuleCategory> _categoryByConfigId = new();

    // Cached diagnostic state for first-sample / diff logging.
    private bool _firstSampleLogged;
    private IReadOnlyDictionary<int, long>? _lastEquippedSnapshot;

    // Pre-resolved Empty results to keep the failure-return path alloc-free.
    private static readonly ModuleSnapshot EmptySnapshot = new(Array.Empty<ModuleInfo>(), 0L);
    private static readonly EquippedSet EmptyEquipped = new(new Dictionary<int, long>(0));
    private static readonly IReadOnlyList<int> EmptyInstalled = Array.Empty<int>();

    public PandaInventoryPullReader(InventoryProbeState state, IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _state = state;
        _log = log;
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// Attaches the game's VContainer <c>IObjectResolver</c> so the reader can
    /// resolve <c>ICharDataObtain</c>/<c>ICharDataWatcherService</c> for the
    /// live <c>CharSerialize</c>. Called by Host once the game root is probed.
    /// Idempotent and safe to call before resolution; if a prior resolution
    /// attempt failed without the resolver, the throttle is reset so the next
    /// poll re-attempts with the container path now available.
    /// </summary>
    public void AttachResolver(object? resolver)
    {
        if (resolver is null) return;
        _objectResolver = resolver;
        // Allow the next EnsureResolved to re-run the (now richer) candidate
        // search if it hadn't already succeeded.
        if (!_resolutionSucceeded)
        {
            _resolutionFailureLogged = false;
        }
    }

    /// <summary>
    /// Clears the resolution backoff so the next few <see cref="EnsureResolved"/>
    /// calls re-run the candidate probe at full speed. Called by Host on a
    /// lifecycle transition (login / scene enter) — that's exactly when the
    /// pull-based path flips from "no character loaded" to "the live
    /// CharDataComponent is readable". Without this, the boot-time fast attempts
    /// (all pre-character) exhaust the fast budget and the probe sits in the
    /// 600-call backoff long past the moment the player actually entered the
    /// world. No-op once resolution has already succeeded.
    /// </summary>
    public void OnLifecycleAdvanced()
    {
        if (_resolutionSucceeded) return;
        _failedResolutionAttempts = 0;
        _resolutionFailureLogged = false;
        // Clear the per-candidate / per-hop log dedup so the in-world probe runs
        // re-surface their outcomes (the boot-time "null" results otherwise mask
        // whether the live-character attempts succeed). Cheap — these sets are
        // tiny and only repopulate while resolution is still unsuccessful.
        ClearProbeLogDedup();
    }

    public bool TryReadModules(out ModuleSnapshot snapshot)
    {
        snapshot = EmptySnapshot;
        if (!EnsureResolved())
        {
            return false;
        }

        var charSerialize = _readCharSerialize!();
        if (charSerialize is null)
        {
            OnReadAbort(ReadAbort.CharSerializeNull);
            return false;
        }

        var modules = ReadModuleList(charSerialize);
        snapshot = new ModuleSnapshot(modules, DateTime.UtcNow.Ticks);
        OnSampleLogged(modules.Count, _lastEquippedSnapshot?.Count ?? 0);
        return true;
    }

    public bool TryReadEquipped(out EquippedSet equipped)
    {
        equipped = EmptyEquipped;
        if (!EnsureResolved())
        {
            return false;
        }

        // Serve the MAINTAINED equipped set: seeded from the last full method-21
        // sync and kept current by method-22 dirty deltas (a manual in-game equip
        // arrives as a dirty delta, never a full sync). Re-reading the captured
        // CharSerialize here would miss those deltas — the captured proto is the
        // last *full* sync, so its ModSlots stay stale after an incremental equip.
        var dict = _state.EquippedSnapshot;
        if (dict is null)
        {
            // No full sync has seeded the set yet — fall back to a direct read so
            // a pull-based (non-capture) build still surfaces the equipped set.
            var charSerialize = _readCharSerialize!();
            if (charSerialize is null)
            {
                OnReadAbort(ReadAbort.CharSerializeNull);
                return false;
            }
            dict = ReadEquippedSlots(charSerialize);
        }

        equipped = new EquippedSet(dict);
        OnEquippedDiffMaybeLogged(dict);
        _lastEquippedSnapshot = dict;
        return true;
    }

    /// <summary>
    /// Reads the local player's equipped Battle Imagine ids from
    /// <c>CharSerialize.Resonance.Installed</c> (proto field 28), in slot order
    /// ([0]=left/X, [1]=right/Z). Returns <c>false</c> when the live
    /// CharSerialize isn't resolvable yet; an absent/empty Resonance is a
    /// successful read of an empty list.
    /// </summary>
    public bool TryReadInstalled(out IReadOnlyList<int> installed)
    {
        installed = EmptyInstalled;
        if (!EnsureResolved())
        {
            return false;
        }

        var charSerialize = _readCharSerialize!();
        if (charSerialize is null)
        {
            OnReadAbort(ReadAbort.CharSerializeNull);
            return false;
        }

        installed = ReadInstalledResonances(charSerialize);
        return true;
    }

    /// <summary>
    /// Reads the current equipped <c>Mod.ModSlots</c> map (slot → uuid) from
    /// the freshest captured <c>CharSerialize</c>, for Phase 7 Task 13
    /// equip-completion polling (B2). Returns <c>null</c> when the container
    /// path isn't resolved yet or no live CharSerialize has been captured —
    /// the equip probe treats that as "not yet observable" and keeps polling
    /// until its deadline. This is nearly free: it reuses the same property
    /// handles the 1Hz refresh already resolved, reading the long-lived proto
    /// object the game keeps mutating in place via SyncContainerDirtyData.
    /// </summary>
    internal IReadOnlyDictionary<int, long>? GetEquippedSlotsForEquipPolling()
    {
        if (!EnsureResolved())
        {
            return null;
        }

        // Prefer the maintained set (current through method-22 dirty deltas, which
        // is exactly what an equip-completion poll is waiting to observe). Fall
        // back to a direct CharSerialize read only before the first full sync has
        // seeded the snapshot.
        var maintained = _state.EquippedSnapshot;
        if (maintained is not null)
        {
            return maintained;
        }

        var charSerialize = _readCharSerialize!();
        if (charSerialize is null)
        {
            return null;
        }

        return ReadEquippedSlots(charSerialize);
    }
}
