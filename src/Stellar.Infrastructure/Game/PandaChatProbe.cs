using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Surgical chat probe. Installs HarmonyX postfixes on every plausible ZRpc
/// receive entry point on <c>ZCode.ZRpc.ZRpcImpl</c> and <c>ZCode.ZRpc.ZRpcCtrl</c>.
/// All postfixes share one filter-first hot-path helper: zero log output and zero
/// allocation on the rejection path, one-shot diagnostic per site so we can see
/// what traffic flows through each hook, and chat projection only when the
/// concrete proto type matches the ChitChat filter.
///
/// Implementation is split across partial files:
/// <list type="bullet">
/// <item><c>PandaChatProbe.cs</c> — composition root: ctor, shared fields, <see cref="RegisterOnWireTap"/>, dedup state.</item>
/// <item><c>PandaChatProbe.Orchestration.cs</c> — <see cref="PatchAll"/>, <see cref="InstallReceivePostfixes"/>, <see cref="TryInstallReceivePostfix"/>.</item>
/// <item><c>PandaChatProbe.Reflection.cs</c> — RPC type / RetMsg helper resolution and assembly-enumeration utilities.</item>
/// <item><c>PandaChatProbe.Send.cs</c> — send-side: TrySend, ZTcpClient.Send PREFIX, ProxyCall PREFIX, packet builders.</item>
/// <item><c>PandaChatProbe.Receive.cs</c> — receive-side: HarmonyX postfix dispatch, ChitChatNtf method=1/3 envelope consumers, all-Returns consumer.</item>
/// <item><c>PandaChatProbe.History.cs</c> — GetChipChatRecords reply parsing + channel attribution.</item>
/// <item><c>PandaChatProbe.Diagnostics.cs</c> — STELLAR_DIAGNOSTICS=1-gated per-event logging.</item>
/// </list>
/// </summary>
internal sealed partial class PandaChatProbe : IChatProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Hard cap on installed hooks — protects the in-world scenario from cascading
    // per-packet work if recon ever expands the candidate set.
    private const int MaxHooks = 5;

    // Static handle the HarmonyX postfixes call back into. Set during PatchAll();
    // null before bootstrap. One-shot — never reset.
    private static PandaChatProbe? Instance;

    // Per-site routed postfix MethodInfos. Each one captures the site tag and the
    // message-arg index in its name; the actual hot-path code lives in
    // OnReceiveMethodCalled (PandaChatProbe.Receive.cs).
    private static readonly MethodInfo PostfixDispatchMethod =
        typeof(PandaChatProbe).GetMethod(nameof(OnReceiveMethodCalled), BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly IPluginLog _log;
    private readonly Harmony _harmony;

    private bool _patched;

    // Per-site one-shot diagnostic. Bounded by the number of installed hooks.
    private readonly ConcurrentDictionary<string, bool> _unmatchedPerSite = new();

    // StubCall-shape accessor cache — only used when the first arg is a concrete
    // StubCall (the AddStubCall site). Other sites use the tolerant ExtractMessage path.
    private PropertyInfo? _stubCallMsgProperty;
    private FieldInfo? _stubCallMsgField;
    private PropertyInfo? _uuidProperty;
    private PropertyInfo? _methodIdProperty;

    // ProxyReturn unwrap helper resolved at PatchAll. Returns the inner IBufferMessage
    // payload from a ProxyReturn wrapper. Signature per recon:
    //   static IBufferMessage GetRetMsg(IProxyReturn proxyReturn, bool flag)
    // Lives on ZCode.ZRpc.ZrpcExtensions, NOT ZRpcImpl. Cached as a static field so
    // the postfix hot path can call it directly.
    private static MethodInfo? _getRetMsgMethod;

    // Cached MethodInfo for Il2CppObjectBase.Cast<TIProxyReturn>() — required because
    // managed reflection refuses to pass a concrete IL2CPP-wrapped ProxyReturn where
    // an IProxyReturn parameter is declared, even though the IL2CPP class implements
    // the interface natively. We Cast<>() the receiver into the interface wrapper type
    // before invoking GetRetMsg.
    private static MethodInfo? _il2cppCastMethod;

    // Per-distinct-concrete-inner-type one-shot diagnostic for ProcessReturn/AddProxyReturn
    // sites. Bounded by the number of distinct inner protobuf types observed (~50-200 in
    // the wild). Lets us see every reply protobuf type the game receives, one log line each.
    private readonly ConcurrentDictionary<string, bool> _unmatchedInnerTypes = new();

    // One-shot diagnostic for GetRetMsg invocation outcome. Without this, an early
    // throw or null return is invisible — the postfix swallows the exception, and the
    // distinct-inner-type diagnostic only sees the unwrapped (or in our case still-
    // wrapped) type. Lets us pinpoint why unwrap is/isn't producing a payload.
    private static int _getRetMsgInvokeLogged;
    private static int _getRetMsgInvokeFailLogged;

    // Chat service UUIDs (low 32 bits — match against the low half of the u64
    // service_uuid in the RPC header). Sourced from BPSR-B's zproto_message_type
    // catalog: ChitChatNtf = server-pushed chat notifications, ChitChat =
    // request/response service for chat send + history fetch. We catch BOTH so
    // we see broadcast chat (Notify) and our own send-replies (Return).
    private const uint ChitChatNtfServiceUuid = 164931432u;
    private const uint ChitChatServiceUuid    = 1321197368u;

    // ChitChat method ids (per BPSR-B src/bpsr_model/chitchat_method_id.py).
    // Returns from the server don't carry a method_id in the wire header — the
    // sender looks it up from a pending-call table keyed by call_id. From
    // postfix-only observation we can't track that table, so for ChitChat
    // Returns we attempt to parse the payload as GetChipChatRecordsReply and
    // skip silently if the protobuf shape doesn't match.
    private const uint GetChipChatRecordsMethodId = 2u;

    // ChitChat.SendChitChatMsg method id (per BPSR-B src/bpsr_model/chitchat_method_id.py).
    private const uint SendChitChatMsgMethodId = 1u;

    // ZprotoMsgTypeId.Call — wire flag identifying a client-initiated Call packet
    // (request expecting a Return). Mirrors BPSR-B's zproto_message_type catalog.
    private const ushort ZprotoMsgTypeIdCall = 1;

    // Reassembly + IL2CPP span extraction live on PandaWireTap (single owner
    // of the TCP recv hook). PandaChatProbe consumes already-parsed
    // WireEnvelope values via the wire-tap dispatch table.

    private readonly IWireTap _wireTap;

    public PandaChatProbe(IPluginLog log, IWireTap wireTap)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _wireTap = wireTap ?? throw new ArgumentNullException(nameof(wireTap));
        _harmony = new Harmony("stellar.chatprobe");
    }

    /// <summary>
    /// Subscribe to the shared wire-tap for ChitChat traffic. Idempotent.
    /// Notifies arrive via <see cref="OnChatNotifyEnvelope"/> (method=1) and
    /// <see cref="OnChatMethod3Envelope"/> (method=3 — empirically the path
    /// stranger/cross-account whispers arrive on; method=1 only carries
    /// traffic with the user's own alts). History Returns (which carry no
    /// service_uuid on the wire) arrive via <see cref="OnAnyReturnEnvelope"/>
    /// and are filtered against the pending call_id table populated by
    /// <see cref="OnTcpClientSend"/>.
    /// </summary>
    public void RegisterOnWireTap()
    {
        _wireTap.Register(ChitChatNtfServiceUuid, methodId: 1u, OnChatNotifyEnvelope);
        _wireTap.Register(ChitChatNtfServiceUuid, methodId: 3u, OnChatMethod3Envelope);
        _wireTap.RegisterReturn(OnAnyReturnEnvelope);
        _log.Info("[ChatProbe] registered ChitChatNtf method=1 + method=3 + all-Returns on shared WireTap");
    }

    public Action<ChatMessage>? OnMessageReceived { get; set; }

    // Cross-path dedup state. The chat history fetch at login returns the
    // recent messages — but those same messages also flow as live Notifies, so
    // any overlap produces visible duplicates in the plugin UI. The actual
    // dedup logic lives in WhisperDedup (Application/Wire/) so the contract
    // can be unit-tested in isolation.
    private readonly WhisperDedup _dedup = new();

    private bool MarkSeen(long senderId, long msgId, long timestampTicks)
        => _dedup.MarkSeen(senderId, msgId, timestampTicks);

    /// <summary>
    /// Advance <see cref="_callIdCounter"/> to at least <paramref name="observed"/>
    /// so subsequent outbound Calls we generate don't collide with call_ids the
    /// game has already issued. CAS loop because Send and TrySend may race.
    /// </summary>
    private void ObserveCallId(uint observed)
    {
        long target = observed;
        long prev;
        do
        {
            prev = System.Threading.Interlocked.Read(ref _callIdCounter);
            if (target <= prev) return;
        } while (System.Threading.Interlocked.CompareExchange(ref _callIdCounter, target, prev) != prev);
    }
}
