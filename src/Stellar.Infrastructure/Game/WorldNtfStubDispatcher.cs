using System;
using System.Collections.Generic;
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
/// Runs on the network receive thread; never throws across the IL2CPP
/// boundary. As of this task the dispatcher is defined but NOT wired in — the
/// combat and inventory probes still own their own hooks. Later tasks migrate
/// them onto <see cref="Register"/> and call <see cref="Install"/> from Host.
/// </para>
/// </summary>
internal sealed partial class WorldNtfStubDispatcher
{
    private const string TargetTypeName = "Zservice.WorldNtfStub";
    private const string TargetMethodName = "OnCallStub";

    private static WorldNtfStubDispatcher? Instance;

    private readonly StubRouter _router = new();

    // WorldNtf catch-all observers — fire for EVERY WorldNtf-uuid packet
    // regardless of method id. Unlike the method-keyed router these are for
    // consumers that detect their packet STRUCTURALLY because the method id is
    // not known offline (e.g. the dungeon probe matching SyncDungeonData). Kept
    // separate so the common method-keyed path stays a single dictionary lookup.
    private readonly List<Action<uint, byte[]>> _observers = new();

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
    /// Registers <paramref name="observer"/> to receive EVERY WorldNtf-uuid
    /// packet as <c>(methodId, payload)</c>, regardless of method id. For
    /// consumers that recognise their packet structurally rather than by a known
    /// method id. Call before <see cref="Install"/>. Observers run after the
    /// method-keyed router on each matching packet.
    /// </summary>
    public void RegisterObserver(Action<uint, byte[]> observer)
    {
        if (observer is not null) _observers.Add(observer);
    }

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
    /// the network receive thread; never throws across the IL2CPP boundary.
    /// </summary>
    private static void OnCallStubPostfix(object?[] __args)
    {
        var d = Instance;
        if (d is null) return;

        // Perf harness: time the per-packet header read + dispatch (runs on the
        // network thread; invisible to the per-frame Update/draw timers). No-op off.
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

        // Cheap-reject: only pay the payload extraction when a method-keyed
        // handler is subscribed OR at least one WorldNtf catch-all observer is
        // registered. Foreign methods with neither still cost only the header read.
        bool routed = _router.Subscribes(methodId);
        if (!routed && _observers.Count == 0) return;

        var bytes = ExtractPayload(stubCall);
        if (bytes is null) return;

        if (routed) _router.Route(methodId, bytes);

        for (int i = 0; i < _observers.Count; i++)
            _observers[i](methodId, bytes);
    }
}
