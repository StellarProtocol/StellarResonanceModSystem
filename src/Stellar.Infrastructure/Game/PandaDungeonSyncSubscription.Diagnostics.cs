using System;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaDungeonSyncSubscription"/>. One-shot lines are
/// ALWAYS-ON (they confirm the subscription installs and fires live — the key
/// uncertainty of a freshly traced seam); per-event repeats gate on
/// <c>STELLAR_DIAGNOSTICS=1</c>. Capture lines run on the game's MessagePipe publish
/// thread — bounded to one line each in steady state so the capture path stays inert.
/// </summary>
internal sealed partial class PandaDungeonSyncSubscription
{
    private bool _firstCaptureLogged;
    private bool _extractFailWarned;
    private bool _pendingLogged;
    private bool _exhaustedWarned;
    private bool _subscribeThrewWarned;

    // One-shot always-on: the first captured delta proves the subscription is live
    // end-to-end (handler fired + bytes extracted). Repeats only under the toggle.
    private void DiagDeltaCaptured(int byteCount)
    {
        if (!_firstCaptureLogged)
        {
            _firstCaptureLogged = true;
            _log.Info($"[DungeonSync] first container delta captured ({byteCount} bytes) — MessagePipe subscription live; parsing at drain");
            return;
        }
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[DungeonSync] delta captured ({byteCount} bytes)");
    }

    // One-shot always-on: the handler fired but the event → BufferStream →
    // ByteString walk failed — the interop surface differs from the recon'd
    // shape and the extraction needs a revisit.
    private void DiagExtractFailed()
    {
        if (_extractFailWarned) return;
        _extractFailWarned = true;
        _log.Warning("[DungeonSync] subscription handler fired but delta bytes could not be extracted (VData/Buffer/ToByteArray walk failed) — interop shape mismatch");
    }

    // Resolution not yet possible (no route to ISubscriber<T> yet). One-shot info at
    // first miss, one-shot warning when the bounded retry budget runs out; the
    // in-between retries stay silent (they run on every gated tick).
    private void DiagSubscribePending()
    {
        if (_attempts >= MaxSubscribeAttempts)
        {
            if (_exhaustedWarned) return;
            _exhaustedWarned = true;
            _log.Warning($"[DungeonSync] MessagePipe subscription unavailable after {_attempts} attempts — giving up; run-clock falls back to method-24 tap / method-55 edge / active_time");
            return;
        }
        if (_pendingLogged) return;
        _pendingLogged = true;
        _log.Info("[DungeonSync] MessagePipe subscriber not resolvable yet (container/global provider not up) — retrying from the framework tick");
    }

    // A resolution/subscribe attempt threw (never propagates — wiring must not take
    // down the host). One-shot; subsequent attempts keep retrying quietly.
    private bool _onSyncWrapLogged;
    private bool _onSyncShapeWarned;

    private void DiagOnSyncWrapped()
    {
        // Log every (re)wrap — a re-wrap after lua re-assignment is signal, not noise.
        _log.Info(_onSyncWrapLogged
            ? "[DungeonSync] OnSync re-wrapped (lua re-assigned the handler)"
            : "[DungeonSync] DungeonSyncService.OnSync wrapped (delegate call-through; no patching) — dirty-delta capture live");
        _onSyncWrapLogged = true;
    }

    // Why-not visibility for the wrap route: one line at ~10s and ~2min in (attempt
    // counts at the 30 Hz tick), naming which precondition is still unmet.
    private void DiagWrapStillPending()
    {
        if (_attempts != 300 && _attempts != 3600) return;
        var status = _wrapService is null
            ? "DungeonSyncService not resolvable from the container yet"
            : "service resolved but OnSync is still null (lua sync module not initialized)";
        _log.Info($"[DungeonSync] OnSync wrap pending after {_attempts} attempts: {status}");
    }

    private void DiagOnSyncShapeMissing(string? detail)
    {
        if (_onSyncShapeWarned) return;
        _onSyncShapeWarned = true;
        _log.Warning($"[DungeonSync] OnSync wrap unavailable ({detail}) — falling back to MessagePipe/stub routes");
    }

    private void DiagSubscribeThrew(Exception ex)
    {
        // Non-blittable-struct delegate conversion is a PERMANENT Il2CppInterop
        // limitation (confirmed live 2026-07-05) — stop retrying instead of
        // burning 18k attempts on a condition that cannot change this session.
        if (ex.InnerException is ArgumentException ae && ae.Message.Contains("non-blittable"))
        {
            // Permanent for the MessagePipe route ONLY — do NOT exhaust the shared
            // attempt budget (that killed the OnSync wrap route on 2026-07-05).
            _messagePipeImpossible = true;
            if (_subscribeThrewWarned) return;
            _subscribeThrewWarned = true;
            _log.Warning("[DungeonSync] MessagePipe route impossible (non-blittable event struct) — OnSync wrap route keeps retrying");
            return;
        }
        if (_subscribeThrewWarned) return;
        _subscribeThrewWarned = true;
        // Unwrap the full inner chain — a bare TargetInvocationException told us
        // nothing on 2026-07-05; the inner exception names the failing interop call.
        var chain = new System.Text.StringBuilder();
        for (var e = ex; e is not null && chain.Length < 600; e = e.InnerException)
            chain.Append(chain.Length > 0 ? " <= " : "").Append(e.GetType().Name).Append(": ").Append(e.Message);
        var site = ex.InnerException?.StackTrace?.Split('\n') is { Length: > 0 } frames ? frames[0].Trim() : "";
        _log.Warning($"[DungeonSync] subscription attempt threw: {chain} @ {site} — retrying from the framework tick");
    }
}
