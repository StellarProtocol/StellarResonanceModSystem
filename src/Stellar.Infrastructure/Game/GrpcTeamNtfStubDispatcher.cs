using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Single owner of the HarmonyX postfix on
/// <c>Zservice.GrpcTeamNtfStub.OnCallStub(ZCode.ZRpc.IStubCall)</c> — the
/// party/team stub packet firehose. Peer of <see cref="WorldNtfStubDispatcher"/>
/// for the GrpcTeamNtf service.
///
/// <para>
/// Reads the <c>IStubCall</c> header once with accessors cached per concrete
/// stub <see cref="Type"/> (GetUuid / GetMethodId / GetCallData), rejects
/// foreign / unsubscribed method IDs before extracting any payload, and routes
/// subscribed method IDs through an internal <see cref="StubRouter"/>.
/// </para>
///
/// <para>
/// Runs on the network receive thread; never throws across the IL2CPP
/// boundary. Register all handlers before calling <see cref="Install"/> so the
/// router is fully populated before any packets arrive (same ordering constraint
/// as <see cref="WorldNtfStubDispatcher"/>).
/// </para>
/// </summary>
internal sealed partial class GrpcTeamNtfStubDispatcher
{
    private const string TargetTypeName   = "Zservice.GrpcTeamNtfStub";
    private const string TargetMethodName = "OnCallStub";

    private static GrpcTeamNtfStubDispatcher? Instance;

    private readonly StubRouter _router = new();
    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _patched;
    private bool _patchSucceeded;
    private bool _getCallDataFailLogged;

    /// <summary>
    /// Returns <see langword="true"/> after a successful <see cref="Install"/>
    /// call — i.e., the postfix was actually applied. Consumers (e.g.,
    /// <see cref="PandaPartyStubProbe"/>) check this to decide whether to
    /// activate a wire-tap fallback.
    /// </summary>
    public bool IsInstalled => _patchSucceeded;

    public GrpcTeamNtfStubDispatcher(IPluginLog log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>
    /// Registers <paramref name="handler"/> for <paramref name="methodId"/>,
    /// replacing any prior registration. Call before <see cref="Install"/>.
    /// </summary>
    public void Register(uint methodId, Action<uint, byte[]> handler) =>
        _router.Register(methodId, handler);

    /// <summary>
    /// Installs the single owner postfix on <c>GrpcTeamNtfStub.OnCallStub</c>.
    /// Idempotent — subsequent calls no-op. On any failure (type not loaded,
    /// method missing, patch threw) logs a warning and returns without throwing.
    /// </summary>
    public void Install(string harmonyId)
    {
        if (_patched) return;
        _patched = true;

        var onCallStub = ResolveCallStubMethod();
        if (onCallStub is null) return;

        var postfix = typeof(GrpcTeamNtfStubDispatcher).GetMethod(
            nameof(OnCallStubPostfix), BindingFlags.Static | BindingFlags.NonPublic);
        if (postfix is null)
        {
            _log.Warning("[TeamStubDispatch] OnCallStubPostfix method missing (build error)");
            return;
        }

        try
        {
            _harmony = new Harmony(harmonyId + ".grpcteamntfstub");
            Instance = this;
            _harmony.Patch(onCallStub, postfix: new HarmonyMethod(postfix));
            _patchSucceeded = true;
            _log.Info("[TeamStubDispatch] single owner installed on GrpcTeamNtfStub.OnCallStub");
        }
        catch (Exception ex)
        {
            _log.Warning($"[TeamStubDispatch] patch failed: {ex.GetType().Name}: {ex.Message}");
            Instance = null;
        }
    }

    /// <summary>
    /// Uninstalls the postfix (called on framework Unload).
    /// </summary>
    public void Uninstall()
    {
        try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }
        _harmony = null;
        Instance = null;
        _patched = false;
    }

    /// <summary>
    /// HarmonyX postfix — fires after <c>GrpcTeamNtfStub.OnCallStub</c> on the
    /// network receive thread. Never throws across the IL2CPP boundary.
    /// </summary>
    private static void OnCallStubPostfix(object?[] __args)
    {
        var d = Instance;
        if (d is null) return;

        var perfT = Stellar.Abstractions.Diagnostics.PerfProbe.HookBegin();
        var perfA = Stellar.Abstractions.Diagnostics.PerfProbe.HookBeginAlloc();
        try
        {
            d.DispatchPacket(__args);
        }
        catch (Exception ex)
        {
            try { d._log.Warning($"[TeamStubDispatch] threw: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* logging itself failed — give up silently */ }
        }
        finally
        {
            Stellar.Abstractions.Diagnostics.PerfProbe.HookEndWire(perfT, perfA);
        }
    }

    // Core per-packet path. Cheap-reject before payload extraction keeps the
    // unsubscribed path allocation-free.
    private void DispatchPacket(object?[] __args)
    {
        if (__args is null || __args.Length < 1) return;
        var stubCall = __args[0];
        if (stubCall is null) return;

        if (!TryReadHeader(stubCall, out var uuid, out var methodId)) return;
        if (uuid != BPSRServiceIds.GrpcTeamNtf || !_router.Subscribes(methodId)) return;

        var bytes = ExtractPayload(stubCall);
        if (bytes is null) return;

        _router.Route(methodId, bytes);
    }
}
