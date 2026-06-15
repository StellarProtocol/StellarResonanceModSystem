using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Push-based live-CharSerialize capture concern of the inventory probe
/// (C-14 collaborator).
///
/// <para>Empirical in-world recon (Phase 7 Task 12) showed every pull-based
/// accessor is unreachable on this build: the VContainer
/// <c>IObjectResolver</c> never attaches off <c>Game.GameRoot</c>,
/// <c>CharDataComponent</c> is a Pure ECS struct whose
/// <c>ZSingleton.Instance</c> yields an empty CharSerialize, and the
/// container-sync message events are not cached on any singleton.</para>
///
/// <para>The live CharSerialize rides the scene connection: the server
/// delivers it as a <c>WorldNtf</c> stub call —
/// <c>SyncContainerData</c> (methodId 21, full sync) and
/// <c>SyncContainerDirtyData</c> (methodId 22, incremental). Rather than
/// patch the fragile <c>Panda.ZGame.ContainerSyncService.SyncContainerData</c>
/// handler (which crashes the game when patched at boot — wrong target), we
/// postfix the CENTRAL scene-connection stub dispatcher
/// <c>Zservice.WorldNtfStub.OnCallStub(IStubCall)</c> — the SAME proven,
/// boot-safe target the combat probe already patches at
/// <c>OnHotUpdateReady</c>. On methodId 21 we decode the raw payload bytes
/// into a <c>Zproto.CharSerialize</c> via the game's generated protobuf
/// parser and latch it. See <c>PandaInventoryWireCapture.StubCapture.cs</c> for the
/// postfix mechanism (mirrors <see cref="PandaCombatStubProbe"/>).</para>
///
/// <para>The capture writes the latched CharSerialize + maintained equipped set
/// through the shared <see cref="InventoryProbeState"/>; it is the
/// highest-priority candidate (<c>candidate 0</c>) in the pull-reader's
/// multi-candidate selector, so it wins the moment a sync arrives. For the
/// reseed fallback this concern calls back into the injected
/// <see cref="PandaInventoryPullReader"/> (<c>ReadEquippedSlots</c>) — a
/// one-directional dependency; the pull-reader never references this type.</para>
/// </summary>
internal sealed partial class PandaInventoryWireCapture
{
    // Shared by the WorldNtf capture-side partials (StubCapture / Decode /
    // ShapeDump diagnostics).
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // The cross-thread mutable state shared with the pull-read concern (the
    // SAME instance both collaborators receive from the façade). The live
    // CharSerialize latch + the capture-hook flag + the maintained equipped
    // snapshot all live here; the volatile latch + set-once flag semantics are
    // preserved verbatim by InventoryProbeState.
    private readonly InventoryProbeState _state;

    // The pull-read collaborator — used for the reseed fallback
    // (ReseedEquippedFromSync → ReadEquippedSlots) and the shared reflection
    // helpers. One-directional back-reference; the pull-reader does NOT
    // reference this type or the façade (acyclic).
    private readonly PandaInventoryPullReader _pullReader;

    // Application-side self-gear cache. Receives the GearInstance list decoded
    // by Stellar.Wire's GearInstanceReader on every method-21 full sync
    // (replace semantics; see StubCapture.DecodeSelfGearFromSync).
    private readonly IGearInstanceSink _gearSink;

    public PandaInventoryWireCapture(
        InventoryProbeState state,
        PandaInventoryPullReader pullReader,
        IPluginLog log,
        IGameTypeRegistry typeRegistry,
        IGearInstanceSink gearSink)
    {
        _state = state;
        _pullReader = pullReader;
        _log = log;
        _typeRegistry = typeRegistry;
        _gearSink = gearSink;
    }
}
