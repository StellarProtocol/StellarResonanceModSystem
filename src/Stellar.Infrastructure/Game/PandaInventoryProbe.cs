using System.Collections.Generic;
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

    public bool TryReadModules(out ModuleSnapshot snapshot) => _pullReader.TryReadModules(out snapshot);

    public bool TryReadEquipped(out EquippedSet equipped) => _pullReader.TryReadEquipped(out equipped);

    /// <summary>
    /// Reads the local player's equipped Battle Imagine ids from
    /// <c>CharSerialize.Resonance.Installed</c> (proto field 28). Forwarded to
    /// the pull-read collaborator, which walks the same latched CharSerialize.
    /// </summary>
    public bool TryReadInstalled(out IReadOnlyList<int> installed) => _pullReader.TryReadInstalled(out installed);

    /// <summary>
    /// Reads the current equipped <c>Mod.ModSlots</c> map (slot → uuid) for
    /// Phase 7 Task 13 equip-completion polling (B2). Forwarded to the pull-read
    /// collaborator.
    /// </summary>
    internal IReadOnlyDictionary<int, long>? GetEquippedSlotsForEquipPolling()
        => _pullReader.GetEquippedSlotsForEquipPolling();
}
