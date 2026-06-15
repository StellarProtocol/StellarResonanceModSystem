using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaChatProbe"/>. Gated behind the
/// <c>STELLAR_DIAGNOSTICS=1</c> environment variable so steady-state hot paths
/// pay a single static-field read; flip it on to enable the per-event chat
/// log lines used during whisper-drop / dedup investigations.
///
/// Entry points called from the main partial file:
/// <list type="bullet">
/// <item><see cref="DiagPatchAllStart"/> — fired once at PatchAll. No-op today; reserved as the hook point for future bring-up diagnostics.</item>
/// <item><see cref="DiagProxyCallObserved"/> — per-batch history ProxyCall observation.</item>
/// <item><see cref="DiagChatReceived"/> — per-message method=1 receive.</item>
/// <item><see cref="DiagChatMethod3Received"/> — per-message method=3 receive.</item>
/// <item><see cref="DiagStubCallReceived"/> — per-stub-call receive.</item>
/// </list>
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ===== Diagnostic state =====

    // Method=3 HEX dump diagnostic. Active only when STELLAR_DIAGNOSTICS=1 was
    // set at process start. Cap of 5 mirrors the BuffInfo recon dump. Decremented
    // via Interlocked so concurrent recv-thread dispatches can't double-log.
    private int _method3HexDumpRemaining
        = StellarDiagnostics.IsEnabled ? 5 : 0;

    // ===== Gated dispatchers (called from PandaChatProbe.cs) =====

    /// <summary>
    /// Called once at <c>PatchAll</c>. Phase 1 bring-up recon sweeps used to
    /// live here; they were deleted in Phase 3c (Step 1) once the chat
    /// receive/send paths stabilised. The method is retained as a stable
    /// hook point — wiring future one-shot diagnostics here costs the
    /// production file zero edits.
    /// </summary>
    private void DiagPatchAllStart()
    {
        // Intentionally empty. See xmldoc above.
    }

    // ===== STELLAR_DIAGNOSTICS=1 per-event diagnostic logs =====
    // Each method is the "subsequent" log branch of a first-seen+verbose pair
    // in the production file. Skip body when STELLAR_DIAGNOSTICS is off so
    // calling them is a single static-field read on the hot path.

    /// <summary>
    /// Diagnostics mode: log every subsequent GetChipChatRecords ProxyCall
    /// observation so each history batch's outbound channel_type can be
    /// correlated with the channel the user perceives in the UI.
    /// </summary>
    private void DiagProxyCallObserved(int channelType)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[ChatProbe] GetChipChatRecords ProxyCall observed: channelType={channelType} (wire={WireProtocol.MapWireChannel(channelType)})");
    }

    /// <summary>
    /// Diagnostics mode: log every subsequent parsed ChitChatNtf method=1
    /// receive. Useful for whisper-drop / dedup investigations.
    /// </summary>
    private void DiagChatReceived(ChatMessage chatMsg)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var dbgText = chatMsg.Text ?? string.Empty;
        if (dbgText.Length > 80) dbgText = dbgText.Substring(0, 80);
        _log.Info($"[Chat] received: channel={chatMsg.Channel} sender='{chatMsg.SenderName}' (id={chatMsg.SenderId}) text='{dbgText}'");
    }

    /// <summary>
    /// Diagnostics mode: tag every method=3 receive so it's distinguishable
    /// from method=1 traffic during whisper-drop investigations.
    /// </summary>
    private void DiagChatMethod3Received(ChatMessage chatMsg)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var dbgText = chatMsg.Text ?? string.Empty;
        if (dbgText.Length > 80) dbgText = dbgText.Substring(0, 80);
        _log.Info($"[Chat method=3] received: channel={chatMsg.Channel} sender='{chatMsg.SenderName}' (id={chatMsg.SenderId}) text='{dbgText}'");
    }

    /// <summary>
    /// Diagnostics mode: log every subsequent stub-call receive. Restores the
    /// per-message visibility used during whisper-drop debugging without
    /// forcing a rebuild.
    /// </summary>
    private void DiagStubCallReceived(object stubCall, string concreteType, ChatChannel channel)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var uuidEarly = ChatPropertyReader.TryReadProp(stubCall, _uuidProperty);
        var methodIdEarly = ChatPropertyReader.TryReadProp(stubCall, _methodIdProperty);
        _log.Info($"[Chat] stubcall received: type={concreteType} channel={channel} uuid={uuidEarly} methodId={methodIdEarly}");
    }

    /// <summary>
    /// Diagnostics mode: hex-dump the first 5 method=3 payloads regardless of
    /// parse outcome. Useful for forensic analysis if the parser silently
    /// produces wrong output. Early-returns are ordered cheapest-first:
    /// env-var check (single static field read), counter check (cheap field
    /// read), then the atomic <see cref="System.Threading.Interlocked.Decrement(ref int)"/>
    /// gate that guarantees concurrent recv-thread dispatches can't double-log.
    /// </summary>
    private void DiagMethod3Hex(ReadOnlySpan<byte> bytes)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_method3HexDumpRemaining <= 0) return;
        if (System.Threading.Interlocked.Decrement(ref _method3HexDumpRemaining) < 0) return;
        DumpMethod3Hex(bytes);
    }

    /// <summary>
    /// Dump a method=3 payload in the same shape as <c>[BuffInfo HEX]</c>:
    /// length-prefixed, space-separated upper-hex bytes, followed by a quick
    /// tag scan so a human reader can eyeball the protobuf shape. Never
    /// throws (a diagnostic must never break the recv path). Only called
    /// from <see cref="DiagMethod3Hex"/>, which enforces the
    /// STELLAR_DIAGNOSTICS=1 gate.
    /// </summary>
    private void DumpMethod3Hex(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 3 + 64);
            sb.Append("[Chat method=3 HEX] len=").Append(bytes.Length).Append(' ');
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            // Tag scan — first few field/wire pairs for quick visual triage.
            sb.Append("  tags=[");
            int pos = 0;
            bool firstTag = true;
            int tagCount = 0;
            while (pos < bytes.Length && tagCount < 8)
            {
                if (!WireProtocol.TryReadTag(bytes, ref pos, out var f, out var w)) break;
                if (!firstTag) sb.Append(',');
                firstTag = false;
                sb.Append(f).Append(':').Append(w);
                tagCount++;
                if (!WireProtocol.SkipField(bytes, ref pos, w)) break;
            }
            sb.Append(']');
            _log.Info(sb.ToString());
        }
        catch { /* diagnostic must never escape into the IL2CPP recv path */ }
    }
}
