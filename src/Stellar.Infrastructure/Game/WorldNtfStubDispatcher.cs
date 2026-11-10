using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Single owner of the HarmonyX postfix on
/// <c>Zservice.WorldNtfStub.OnCallStub(ZCode.ZRpc.IStubCall)</c> — the per-
/// packet stub firehose. Replaces the duplicate postfixes that the combat and
/// inventory probes each install today: instead of N hooks each doing per-
/// packet UNCACHED reflection (<c>Type.GetMethod("GetUuid"/...)</c> +
/// <c>Invoke</c>) to read the stub header, this dispatcher reads the header
/// ONCE with accessors cached per concrete stub <see cref="Type"/>, rejects
/// foreign / unsubscribed methods before touching any payload, and routes
/// subscribed methodIds through an internal <see cref="StubRouter"/>.
///
/// <para>
/// Runs on the game's MAIN thread — the game dispatches downstream RPC frames
/// from <c>ZRpcImpl.ProcessFrameDownPackets</c> inside its frame loop (proven
/// by WPR), NOT on a network receive thread — so every microsecond here is
/// frame time. Never throws across the IL2CPP boundary. Host wires it
/// (<c>Wiring.Wire.cs</c>): probes <see cref="Register"/>, then
/// <see cref="Install"/>.
/// </para>
/// </summary>
internal sealed partial class WorldNtfStubDispatcher
{
    private const string TargetTypeName = "Zservice.WorldNtfStub";
    private const string TargetMethodName = "OnCallStub";

    private static WorldNtfStubDispatcher? Instance;

    // Handlers attributed per name on a [Hitch] line (combatTop=) — this postfix feeds the combatHooks total.
    private readonly StubRouter _router = new(Stellar.Abstractions.Diagnostics.HitchProbe.HookKindCombat);

    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _patched;
    private bool _getCallDataFailLogged;

    public WorldNtfStubDispatcher(IPluginLog log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>
    /// Registers <paramref name="handler"/> for <paramref name="methodId"/>,
    /// replacing any prior registration. Delegates to the internal
    /// <see cref="StubRouter"/>. Call before <see cref="Install"/>.
    /// </summary>
    public void Register(uint methodId, Action<uint, byte[]> handler) =>
        _router.Register(methodId, handler);

    /// <summary>
    /// Installs the single owner postfix. Idempotent — subsequent calls no-op.
    /// On any failure (type not loaded, method missing, patch threw) logs a
    /// warning and returns without throwing so it can't take down the host.
    /// </summary>
    public void Install(string harmonyId)
    {
        if (_patched) return;
        _patched = true;

        var onCallStub = ResolveCallStubMethod();
        if (onCallStub is null) return;

        var postfix = typeof(WorldNtfStubDispatcher).GetMethod(
            nameof(OnCallStubPostfix), BindingFlags.Static | BindingFlags.NonPublic);
        if (postfix is null)
        {
            _log.Warning("[StubDispatch] OnCallStubPostfix method missing (build error)");
            return;
        }

        try
        {
            _harmony = new Harmony(harmonyId + ".worldntfstub");
            Instance = this;
            _harmony.Patch(onCallStub, postfix: new HarmonyMethod(postfix));
            _log.Info("[StubDispatch] single owner installed on WorldNtfStub.OnCallStub");
        }
        catch (Exception ex)
        {
            _log.Warning($"[StubDispatch] patch failed: {ex.GetType().Name}: {ex.Message}");
            Instance = null;
        }
    }

    /// <summary>
    /// HarmonyX postfix. Reads the stub header once with cached accessors,
    /// cheaply rejects foreign / unsubscribed packets before extracting any
    /// payload, then routes subscribed methodIds through the router. Runs on
    /// the game's main thread (inside <c>ZRpcImpl.ProcessFrameDownPackets</c>);
    /// never throws across the IL2CPP boundary.
    /// </summary>
    private static void OnCallStubPostfix(object?[] __args)
    {
        var d = Instance;
        if (d is null) return;

        // Perf harness: time the per-packet header read + dispatch. It runs on the
        // MAIN thread but inside the game's RPC pump, outside the per-frame
        // Update/draw timers — this hook timer is the only place it shows. No-op off.
        var _perfT = Stellar.Abstractions.Diagnostics.PerfProbe.HookBegin();
        var _perfA = Stellar.Abstractions.Diagnostics.PerfProbe.HookBeginAlloc();
        try
        {
            d.DispatchPacket(__args);
        }
        catch (Exception ex)
        {
            // Last-resort guard — never let an exception escape into IL2CPP.
            try { d._log.Warning($"[StubDispatch] threw: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* logging itself failed — give up silently */ }
        }
        finally
        {
            Stellar.Abstractions.Diagnostics.PerfProbe.HookEndCombat(_perfT, _perfA);
        }
    }

    // Core per-packet path, extracted so the postfix wrapper stays small.
    // Cheap-reject before any payload extraction keeps the unsubscribed path
    // allocation-free.
    private void DispatchPacket(object?[] __args)
    {
        if (__args is null || __args.Length < 1) return;
        var stubCall = __args[0];
        if (stubCall is null) return;

        if (!TryReadHeader(stubCall, out var uuid, out var methodId)) return;
        if (uuid != BPSRServiceIds.WorldNtf) return;

        DiagCensus(methodId, stubCall);   // NEW — read-only WorldNtf methodId census

        // Cheap-reject: only pay the payload extraction when a method-keyed
        // handler is subscribed. Foreign/unsubscribed methods cost only the
        // header read.
        if (!_router.Subscribes(methodId)) return;

        var bytes = ExtractPayload(stubCall);
        if (bytes is null) return;

        _router.Route(methodId, bytes);
    }
}
