using System.Collections.Generic;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IInventoryProbe"/> façade. Composes the two
/// inventory concerns introduced by the C-14 split and delegates to them:
/// <list type="bullet">
///   <item><see cref="PandaInventoryPullReader"/> — the reflection pull-read
///         concern that walks the live <c>CharSerialize</c> for the module
///         inventory + equipped set.</item>
///   <item><see cref="PandaInventoryWireCapture"/> — the WorldNtf stub-capture
///         concern that latches the method-21 full sync and maintains the
///         equipped set through method-22 dirty deltas.</item>
/// </list>
///
/// <para>Both collaborators share the same <see cref="InventoryProbeState"/>
/// instance (constructed here). The dependency direction is acyclic — the
/// façade constructs <c>state</c>, then <c>pullReader</c>, then
/// <c>wireCapture(state, pullReader)</c>; WireCapture references the pull-reader
/// for the reseed fallback, the pull-reader references neither.</para>
///
/// <para>This type stays thin: lifecycle / dispatcher registration plus the
/// public read API, each forwarded to the owning collaborator. The Host wiring
/// (ctor + Start surface) is unchanged by the split.</para>
/// </summary>
internal sealed class PandaInventoryProbe : IInventoryProbe, IResonanceProbe
{
    // Cross-thread mutable state shared by the pull-read and stub-capture
    // concerns (equipped snapshot + captured CharSerialize latch + capture-hook
    // flag). The SAME instance is injected into both collaborators; the exact
    // thread-visibility contract (volatile COW for the two reference fields,
    // set-once latch for CaptureHookActive) lives inside it.
    private readonly InventoryProbeState _state = new();

    // The reflection pull-read collaborator. Owns resolution + the property walk.
    private readonly PandaInventoryPullReader _pullReader;

    // The WorldNtf stub-capture collaborator. Owns the dispatcher registration,
    // decode, and dirty-delta maintenance.
    private readonly PandaInventoryWireCapture _wireCapture;

    public PandaInventoryProbe(IPluginLog log, IGameTypeRegistry typeRegistry, IGearInstanceSink gearSink)
    {
        _pullReader = new PandaInventoryPullReader(_state, log, typeRegistry);
        _wireCapture = new PandaInventoryWireCapture(_state, _pullReader, log, typeRegistry, gearSink);
    }

    /// <summary>
    /// Subscribes the capture collaborator to <paramref name="dispatcher"/> for
    /// WorldNtf method 21 (<c>SyncContainerData</c>) + method 22
    /// (<c>SyncContainerDirtyData</c>). Called by Host before
    /// <c>WorldNtfStubDispatcher.Install</c>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher) => _wireCapture.RegisterWith(dispatcher);

    /// <summary>
    /// Attaches the game's VContainer <c>IObjectResolver</c>. Forwarded to the
    /// pull-read collaborator. Called by Host once the game root is probed.
    /// </summary>
    public void AttachResolver(object? resolver) => _pullReader.AttachResolver(resolver);

    /// <summary>
    /// Clears the resolution backoff on a lifecycle transition (login / scene
    /// enter). Forwarded to the pull-read collaborator.
    /// </summary>
    public void OnLifecycleAdvanced() => _pullReader.OnLifecycleAdvanced();

    // ── Generation-gated read cache (game-thread only) ──
    // The three TryRead* methods are polled at 1Hz. Rebuilding each snapshot on every poll is the
    // framework's dominant steady-state allocation (measured ~1.8MB/s in a dungeon → periodic GC
    // hitch). The underlying data changes ONLY when a sync bumps InventoryProbeState.Generation, so
    // we serve the last successful build until the generation moves. The `|| !_ok` guard keeps
    // retrying while a read has never succeeded, so resolution/data that comes online later (before
    // any capture bumps the generation) is still picked up. Cache fields are touched only here, on
    // the game thread; Generation is a volatile read of an Interlocked-bumped counter.
    private long _mGen = long.MinValue; private bool _mOk; private ModuleSnapshot _mSnap = null!;
    private long _eGen = long.MinValue; private bool _eOk; private EquippedSet _eSet = null!;

    public bool TryReadModules(out ModuleSnapshot snapshot)
    {
        long gen = _state.Generation;
        if (gen != _mGen || !_mOk)
        {
            _mOk = _pullReader.TryReadModules(out _mSnap);
            if (_mOk) _mGen = gen;
        }
        snapshot = _mSnap;
        return _mOk;
    }

    public bool TryReadEquipped(out EquippedSet equipped)
    {
        long gen = _state.Generation;
        if (gen != _eGen || !_eOk)
        {
            _eOk = _pullReader.TryReadEquipped(out _eSet);
            if (_eOk) _eGen = gen;
        }
        equipped = _eSet;
        return _eOk;
    }

    /// <summary>
    /// Reads the local player's equipped Battle Imagine ids from
    /// <c>CharSerialize.Resonance.Installed</c> (proto field 28). Forwarded to
    /// the pull-read collaborator, which walks the same latched CharSerialize.
    /// </summary>
    private long _iGen = long.MinValue; private bool _iOk; private IReadOnlyList<int> _iList = System.Array.Empty<int>();

    public bool TryReadInstalled(out IReadOnlyList<int> installed)
    {
        long gen = _state.Generation;
        if (gen != _iGen || !_iOk)
        {
            _iOk = _pullReader.TryReadInstalled(out _iList);
            if (_iOk) _iGen = gen;
        }
        installed = _iList;
        return _iOk;
    }

    /// <summary>
    /// Reads the current equipped <c>Mod.ModSlots</c> map (slot → uuid) for
    /// Phase 7 Task 13 equip-completion polling (B2). Forwarded to the pull-read
    /// collaborator.
    /// </summary>
    internal IReadOnlyDictionary<int, long>? GetEquippedSlotsForEquipPolling()
        => _pullReader.GetEquippedSlotsForEquipPolling();

    /// <summary>
    /// Returns the live <c>CharSerialize</c> record (or null before resolution /
    /// first sync). Forwarded to the pull-read collaborator, which already owns
    /// the resolved accessor. Consumed by <see cref="PandaCharIdentityReader"/>
    /// so the player-state probe can serve identity that survives a world-entity
    /// attribute blackout.
    /// </summary>
    internal object? TryGetLiveCharSerialize() => _pullReader.TryGetLiveCharSerialize();

    /// <summary>The CURRENT LIVE equipped gear + modules from the game's containers (reflects manual
    /// equips + class-swap re-equips). Forwarded to the pull-read collaborator.</summary>
    public EquippedLoadout GetLiveEquipped()
    {
        var (gear, modules) = _pullReader.ReadLiveEquipped();
        return new EquippedLoadout(gear, modules);
    }

    /// <summary>
    /// Resolves EVERY saved loadout's PER-CLASS gear + modules from their slot → uuid maps (the loadout
    /// probe's Lua read of <c>equipInfoMap</c>/<c>modInfoMap</c>), in one pass. Forwarded to the pull-read
    /// collaborator, which owns the <c>itemPackage</c> reflection. Lets the loadout probe surface each
    /// class's real gear/modules — the live self-gear/module APIs are class-blind (a class swap never
    /// re-broadcasts them; <c>recon/loadout-switch-findings.md</c> § Phase 0).
    /// </summary>
    internal IReadOnlyList<(IReadOnlyList<GearInstance> Gear, IReadOnlyDictionary<int, ModuleInfo> Modules)>
        ResolvePlanLoadouts(
            IReadOnlyList<(IReadOnlyDictionary<int, long> Equip, IReadOnlyDictionary<int, long> Mod)> plans)
        => _pullReader.ResolvePlanLoadouts(plans);

    /// <summary>True once the live CharSerialize container is reachable — forwarded to the pull-read
    /// collaborator's already-resolved accessor (see <see cref="TryGetLiveCharSerialize"/>).</summary>
    public bool IsResolved => _pullReader.TryGetLiveCharSerialize() is not null;

    /// <summary>
    /// UNUSED / demoted (owner-verified 2026-08-19) — no longer wired to <c>IDeepSlumberProbe</c>.
    /// This C# <c>CharSerialize</c> reflection mirror populates LAZILY (empty until the player opens
    /// the Psychoscope UI at least once this session), so a fresh session's archive uploaded no
    /// Deep-Slumber block. <c>Host</c> now wires <c>DeepSlumberService</c> to
    /// <see cref="PandaLoadoutProbe"/>'s Lua-bridge reader instead (see
    /// <c>Stellar.Host.Wiring.Loadout.cs</c>), which reads the SAME containers via the game's Lua
    /// mirror — populated at login, the source the game's own season views read. Kept (not deleted)
    /// as a reflection-walk reference / potential fallback; forwarded to the pull-read collaborator,
    /// which still owns the reflection walk.
    /// </summary>
    internal DeepSlumberState? Read() => _pullReader.ReadDeepSlumber();
}
