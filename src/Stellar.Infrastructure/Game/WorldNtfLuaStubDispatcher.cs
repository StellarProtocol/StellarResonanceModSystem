using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Single owner of the HarmonyX postfix on
/// <c>ZCode.ZRpc.ZLuaStub.OnCallStub(ZCode.ZRpc.IStubCall)</c> — the LUA-side
/// stub firehose. Peer of <see cref="WorldNtfStubDispatcher"/>, which owns the
/// C# <c>Zservice.WorldNtfStub</c> stub.
///
/// <para>
/// BPSR routes a WorldNtf packet to EITHER the C# stub (methods with a C#
/// handler: combat AOI, inventory, scene) OR the shared Lua stub (Lua-only
/// methods: the dungeon ready-check 70/71, quests, UI notifies). The C# stub
/// never sees 70/71, so the ready-check probe must subscribe HERE. ZLuaStub is
/// shared by every Lua service, so this dispatcher filters
/// <c>uuid == WorldNtf</c> before routing.
/// </para>
///
/// <para>
/// Runs on the network receive thread; never throws across the IL2CPP boundary.
/// Register handlers before <see cref="Install"/>.
/// </para>
/// </summary>
internal sealed partial class WorldNtfLuaStubDispatcher
{
    private const string TargetTypeName = "ZCode.ZRpc.ZLuaStub";
    private const string TargetMethodName = "OnCallStub";

    private static WorldNtfLuaStubDispatcher? Instance;

    private readonly StubRouter _router = new();
    private readonly IPluginLog _log;
    private Harmony? _harmony;
    private bool _patched;
    private bool _getCallDataFailLogged;

    public WorldNtfLuaStubDispatcher(IPluginLog log) =>
        _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>Registers <paramref name="handler"/> for <paramref name="methodId"/>. Call before <see cref="Install"/>.</summary>
    public void Register(uint methodId, Action<uint, byte[]> handler) =>
        _router.Register(methodId, handler);

    /// <summary>Installs the single owner postfix on <c>ZLuaStub.OnCallStub</c>. Idempotent; never throws.</summary>
    public void Install(string harmonyId)
    {
        if (_patched) return;
        _patched = true;

        var onCallStub = ResolveCallStubMethod();
        if (onCallStub is null) return;

        var postfix = typeof(WorldNtfLuaStubDispatcher).GetMethod(
            nameof(OnCallStubPostfix), BindingFlags.Static | BindingFlags.NonPublic);
        if (postfix is null)
        {
            _log.Warning("[LuaStubDispatch] OnCallStubPostfix method missing (build error)");
            return;
        }

        try
        {
            _harmony = new Harmony(harmonyId + ".worldntfluastub");
            Instance = this;
            _harmony.Patch(onCallStub, postfix: new HarmonyMethod(postfix));
            _log.Info("[LuaStubDispatch] single owner installed on ZLuaStub.OnCallStub");
        }
        catch (Exception ex)
        {
            _log.Warning($"[LuaStubDispatch] patch failed: {ex.GetType().Name}: {ex.Message}");
            Instance = null;
        }
    }

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
            try { d._log.Warning($"[LuaStubDispatch] threw: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* logging itself failed — give up silently */ }
        }
        finally
        {
            Stellar.Abstractions.Diagnostics.PerfProbe.HookEndWire(perfT, perfA);
        }
    }

    private void DispatchPacket(object?[] __args)
    {
        if (__args is null || __args.Length < 1) return;
        var stubCall = __args[0];
        if (stubCall is null) return;

        if (!TryReadHeader(stubCall, out var uuid, out var methodId)) return;
        if (uuid != BPSRServiceIds.WorldNtf || !_router.Subscribes(methodId)) return;

        var bytes = ExtractPayload(stubCall);
        if (bytes is null) return;

        _router.Route(methodId, bytes);
    }
}
