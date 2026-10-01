using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Diagnostics only (STELLAR_DIAGNOSTICS=1; nothing is patched otherwise): while any person is selected for posing, logs
/// every outgoing RPC — <c>ZCode.ZRpc.ZRpcImpl.SendMsg(uuid, stubId, callId, methodId, msg, framePack)</c> and
/// <c>SendBytes(…, methodId, bytes, size, framePack)</c> (each the only overload in release_3.7, ilspycmd), the hooks probe
/// run 4 proved fire — as <c>[Posing.Send] svc=&lt;uuid&gt; method=&lt;id&gt;</c>, so the smoke can prove copies and NPC
/// models send nothing (recon § 3: NewMove 131077, PlayAction 21, PlayEmote 8; ReqServerTime 5 and SyncProjectList
/// 278533 are periodic).
/// </summary>
internal sealed class PosingSendTap
{
    internal const string RpcType = "ZCode.ZRpc.ZRpcImpl";
    private const int UuidArg = 0, MethodArg = 3, MinArgs = 6;

    private readonly IGameTypeRegistry _types;
    private readonly Func<bool> _posing;
    private readonly IPluginLog _log;

    public PosingSendTap(IGameTypeRegistry types, Func<bool> posing, IPluginLog log)
    {
        _types = types;
        _posing = posing;
        _log = log;
    }

    public void Install(HarmonyGameMethodHooker hooker)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_types.FindType(RpcType) is not { } rpc) { _log.Warning("[Posing] send tap: ZRpcImpl not found"); return; }
        hooker.PrefixAllOverloads(rpc, "SendMsg", OnSend);
        hooker.PrefixAllOverloads(rpc, "SendBytes", OnSend);
        _log.Info("[Posing] send tap armed");
    }

    private void OnSend(object? _, object?[] args)
    {
        if (args.Length < MinArgs || !_posing()) return;
        _log.Info($"[Posing.Send] svc={args[UuidArg]} method={args[MethodArg]}");
    }
}
