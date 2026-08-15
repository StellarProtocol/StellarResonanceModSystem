using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

internal sealed partial class GameAssetsService
{
    private const int MaxIconRetries = 4;       // reload attempts after a load fault before giving up permanently
    private const int RetryBackoffFrames = 30;  // backoff = RetryBackoffFrames * attempt (~0.5s, 1s, 1.5s… at 60fps)

    // A Failed slot: re-issue the load up to MaxIconRetries times, backoff-gated, THEN stay Failed permanently.
    // Only load FAULTS retry (Path retained) — a no-IconPath data gap (Path == null) is a genuine miss, never retried.
    // Orthogonal to the Item Sprite one-shot (RetriedAlternate): the counter is carried across re-issues so the two
    // don't loop-guard each other — an Item slot still gets its Sprite fallback AND these bounded reloads.
    private void TryRetryFailedIcon(Dictionary<int, Slot> slots, int key, Slot slot, IconKind kind)
    {
        if (slot.Path is null) return;                       // no-address gap → permanent, never retry
        if (slot.Attempts >= MaxIconRetries)
        {
            if (!slot.RetryExhaustedLogged)
            {
                slot.RetryExhaustedLogged = true;
                _log.Warning($"[GameAssets][icon] giving up {LabelOf(kind)}={key} after {slot.Attempts} attempts");
            }
            return;
        }
        if (UnityEngine.Time.frameCount < slot.NextRetryFrame) return;   // backoff not elapsed

        var attempts = slot.Attempts + 1;
        _log.Info($"[GameAssets][icon] requested {LabelOf(kind)}={key} retry attempt {attempts}");
        var retry = BeginLoad(slot.Path, key, kind);        // re-issue the load (re-resolvable from retained Path)
        retry.Attempts = attempts;
        retry.RetriedAlternate = slot.RetriedAlternate;      // preserve the Item Sprite-one-shot state across reloads
        retry.NextRetryFrame = UnityEngine.Time.frameCount + RetryBackoffFrames * attempts;
        slots[key] = retry;
    }

    // Poll the UniTask<Sprite> status for a slot that is in Loading state.
    // UniTaskStatus enum values: 0=Pending 1=Succeeded 2=Faulted 3=Canceled.
    // Updates slots[key] on completion or failure; returns the texture
    // if the load just succeeded, null otherwise.
    //
    // Item icon fallback: the Sprite guess falls back to Texture2D once. If the
    // Sprite load faults/cancels/throws or resolves to the wrong type, the slot
    // retries with LoadAssetAsync<Texture2D> before being memoized Failed.
    private Texture2D? PollLoadingSlot(Dictionary<int, Slot> slots, Slot slot, int key, IconKind kind)
    {
        var label = LabelOf(kind);
        try
        {
            if (slot.UniTask is null) return null;
            var (statusGet, getAwaiter, getResult) = UniTaskOps(slot.UniTask);
            if (statusGet is null) return null;
            var statusObj = statusGet.Invoke(slot.UniTask, null);
            int status = statusObj is null ? 0 : (int)statusObj;
            if (status == 0) return null;  // still pending

            if (status == 1 && getAwaiter is not null && getResult is not null)
            {
                var awaiter = getAwaiter.Invoke(slot.UniTask, null);
                var result = awaiter is null ? null : getResult.Invoke(awaiter, null);
                ResolveResult(slot, key, label, result);
                if (slot.State == LoadState.Failed)
                    RetryItemAlternate(slots, slot, key, kind, "Texture2D load failed");
                else
                    slots[key] = slot;
                return slot.Texture;
            }

            // Faulted / Canceled.
            string exDetail = status == 2 ? FaultDetail(slot.UniTask, getAwaiter, getResult) : "";
            slot.State = LoadState.Failed;
            slot.UniTask = null;
            _log.Warning($"[GameAssets][icon] load failed {label}={key} status={status}{exDetail}");
            RetryItemAlternate(slots, slot, key, kind, $"Texture2D load failed status={status}");
            return null;
        }
        catch (Exception ex)
        {
            slot.State = LoadState.Failed;
            slot.UniTask = null;
            _log.Warning($"[GameAssets][icon] poll threw for {label}={key}: {ex.GetType().Name}: {ex.Message}");
            RetryItemAlternate(slots, slot, key, kind, $"poll threw {ex.GetType().Name}");
            return null;
        }
    }

    // One-shot fallback: if this is a first-failure on an Item OR Path slot, retry with the SPRITE
    // loader (both load Texture2D-first; the retry covers an atlased asset). Sets RetriedAlternate so
    // it can't loop. Always writes the final slot state into slots[key].
    private void RetryItemAlternate(Dictionary<int, Slot> slots, Slot slot, int key, IconKind kind, string reason)
    {
        if ((kind != IconKind.Item && kind != IconKind.Path) || slot.RetriedAlternate || slot.Path is null)
        {
            slots[key] = slot;
            return;
        }

        _log.Info($"[GameAssets][icon] {LabelOf(kind)}={key} {reason} — retrying as Sprite");
        var retrySlot = BeginLoadSpriteRetry(slot.Path, key);
        retrySlot.RetriedAlternate = true;
        slots[key] = retrySlot;
    }

    // Kick off a Sprite load for an address (used by the item-icon retry path).
    private Slot BeginLoadSpriteRetry(string address, int key)
    {
        if (!ResolveOnce() || _loadAssetAsyncString is null)
            return new Slot { State = LoadState.Failed, RetriedAlternate = true };
        if (!MintCancelToken(key, "item", out var token))
            return new Slot { State = LoadState.Failed, RetriedAlternate = true };
        try
        {
            var unitask = _loadAssetAsyncString.Invoke(_loaderInstance, new object[] { address, token, 0, false });
            if (unitask is null)
            {
                _log.Warning($"[GameAssets][icon] Sprite retry returned null for item={key} path='{address}'");
                return new Slot { State = LoadState.Failed, RetriedAlternate = true };
            }
            _log.Info($"[GameAssets][icon] requested item={key} path='{address}' (Sprite retry)");
            return new Slot { State = LoadState.Loading, UniTask = unitask, Path = address, RetriedAlternate = true };
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            _log.Warning($"[GameAssets][icon] Sprite retry threw for item={key}: {inner.GetType().Name}: {inner.Message}");
            return new Slot { State = LoadState.Failed, RetriedAlternate = true };
        }
    }

    // Surface the underlying exception of a faulted UniTask (e.g. "address not found") for the log.
    private static string FaultDetail(object uniTask, MethodInfo? getAwaiter, MethodInfo? getResult)
    {
        if (getAwaiter is null || getResult is null) return "";
        try
        {
            var awaiter = getAwaiter.Invoke(uniTask, null);
            if (awaiter is not null) getResult.Invoke(awaiter, null);
        }
        catch (Exception faulted)
        {
            var inner = faulted is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : faulted;
            return $" cause={inner.GetType().FullName}: {inner.Message}";
        }
        return "";
    }

    // Convert the LoadAssetAsync<Sprite|Texture2D> result into a (Texture2D, UV-rect)
    // pair stored on the slot. The result is a UnityEngine.Sprite, a standalone
    // Texture2D, or null. textureRect is in pixel coordinates (bottom-left origin);
    // UV coordinates are normalised 0..1 and also bottom-left (UV convention), so
    // this is a straight normalise with NO y-flip. (An earlier 1-(y+h)/th flip
    // incorrectly assumed top-left origin and sampled the wrong atlas band → garbled icon.)
    private void ResolveResult(Slot slot, int key, string label, object? result)
    {
        if (result is null)
        {
            slot.State = LoadState.Failed;
            slot.UniTask = null;
            _log.Warning($"[GameAssets][icon] loaded {label}={key} but result was null");
            return;
        }

        // Standalone Texture2D (skill/imagine icons under ui/textures/): use directly, full-rect UV.
        if (result is Texture2D directTex)
        {
            directTex.hideFlags = HideFlags.HideAndDontSave;
            // Trilinear (vs default Point/Bilinear) so the icon stays smooth when scaled up (e.g. the Large
            // Battle-Imagine size) instead of blocky — see reference_loaded_image_texture_filtering.
            directTex.filterMode = FilterMode.Trilinear;
            slot.Texture = directTex;
            slot.Uv = new UvRect(0f, 0f, 1f, 1f);
            slot.State = LoadState.Loaded;
            slot.UniTask = null;
            _log.Info($"[GameAssets][icon] loaded {label}={key} texture='{directTex.name}' {directTex.width}x{directTex.height}");
            return;
        }

        if (result is Sprite sprite) { ResolveSprite(slot, key, label, sprite); return; }

        slot.State = LoadState.Failed;
        slot.UniTask = null;
        _log.Warning($"[GameAssets][icon] loaded {label}={key} but result was {result.GetType().FullName}, not Sprite/Texture2D");
    }
}
