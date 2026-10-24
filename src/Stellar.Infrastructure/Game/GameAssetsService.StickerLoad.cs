using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Chat-sticker thumbnail loading for <see cref="GameAssetsService"/>. Address building + per-language localization
/// live in GameAssetsService.StickerLoc.cs.
///
/// <para><b>Frame cost (2026-10-24).</b> A sticker grid / a history full of sticker bubbles polls dozens of NEW ids in
/// one apply. Three things keep that off the frame:</para>
/// <list type="bullet">
///   <item><b>Resolved cache</b> — <see cref="_stickerReady"/> (id → texture + uv) and <see cref="_stickerDead"/> (gave
///     up): after the first resolve a poll is one dictionary hit, no per-frame key strings, no slot walk.</item>
///   <item><b>Localized-miss cache</b> — <see cref="_stickerLocMiss"/>: an id whose per-language bundle doesn't exist
///     goes straight to the base address on every later call; the localized address is probed once per session.
///     The probe itself passes <c>allowFail=true</c> (BeginLoad): with false the game's
///     <c>loadAssetAsyncInternal</c> runs <c>ZDebug.LogError("…fail, asset not exist")</c> → Unity
///     <c>Debug.LogError</c> WITH a stack trace, synchronously inside our apply, for every base-only sticker.
///     allowFail gates ONLY that LogError — same lookups, still Succeeded(null) (disassembly, 2026-10-24).</item>
///   <item><b>Start budget</b> — at most <see cref="MaxStickerStartsPerFrame"/> new load requests per rendered frame
///     and none once <see cref="StickerFrameBudgetMs"/> of sticker work has been spent this frame; an over-budget id
///     returns null (the element shows its empty same-size box) and starts on a later frame.</item>
/// </list>
/// A session summary (<see cref="MaybeLogStickerSummary"/>) and the <c>assets:sticker</c> [Hitch] bucket verify it.
/// </summary>
internal sealed partial class GameAssetsService
{
    private const int MaxStickerStartsPerFrame = 6;     // new LoadAssetAsync requests per rendered frame
    private const double StickerFrameBudgetMs = 3.0;    // no new start once this much sticker work ran this frame

    private readonly Dictionary<int, (Texture2D Tex, UvRect Uv)> _stickerReady = new();
    private readonly HashSet<int> _stickerDead = new();     // base load gave up (bad Res / missing asset) — no retry
    private readonly HashSet<int> _stickerLocMiss = new();  // no per-language bundle → base only
    private readonly HashSet<int> _quietPathKeys = new();   // path slots whose routine Info lines are summarised instead
    private readonly Dictionary<int, StickerKeys> _stickerKeys = new();

    private int _stickerFrame = -1, _stickerStartsThisFrame;
    private double _stickerFrameMs;
    private readonly StickerStats _stickerStats = new();

    /// <inheritdoc/>
    public object? LoadStickerIcon(int stickerConfigId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (stickerConfigId <= 0) return null;
        // Hot path (every visible sticker, every apply): resolved once → one lookup. (No Unity fake-null check here —
        // that is a native call per poll; the slot path returned a destroyed texture as-is too.)
        if (_stickerReady.TryGetValue(stickerConfigId, out var ready)) { uv = ready.Uv; return ready.Tex; }
        if (_stickerDead.Contains(stickerConfigId)) return null;

        var t0 = Stopwatch.GetTimestamp();
        var tex = LoadStickerSlow(stickerConfigId);
        NoteStickerFrameCost(t0);
        if (tex is not null && _stickerReady.TryGetValue(stickerConfigId, out ready)) uv = ready.Uv;   // set by StickerAttempt
        return tex;
    }

    // The not-yet-resolved path: (localized →) base attempts through LoadByPathKeyed, each its own slot.
    private Texture2D? LoadStickerSlow(int id)
    {
        // Resolve + cache the (localized, base) addresses for the current client language. Cache ONLY once the
        // language word is known (wordReady) — an early call before LocalizationMgr is up resolves a base-only shape
        // without caching, so a later frame upgrades to the localized address (StickerLoc.cs).
        if (!_stickerAddrs.TryGetValue(id, out var addrs))
        {
            var res = TryResolveStickerRes(id);                  // table not ready / unknown id / empty Res → retry
            if (string.IsNullOrEmpty(res)) return null;          // null WITHOUT caching, like the id-based loaders
            addrs = BuildStickerAddresses(res!, out var wordReady);
            if (wordReady) _stickerAddrs[id] = addrs;
        }
        var keys = KeysFor(id);

        // Simplified-Chinese / base client (or language not up yet): the unprefixed base bundle (Path kind keeps the
        // Texture2D-first + Sprite fallback for any atlased sticker).
        if (!addrs.HasLocalized) return StickerAttempt(id, keys.Plain, addrs.Base, IconKind.Path, out _);

        // Non-Chinese client: the per-language bundle first, unless it already missed this session. IconKind.Sticker
        // fails fast (no Sprite retry, no bounded reload) so a base-only sticker surfaces as Failed at once...
        if (!_stickerLocMiss.Contains(id))
        {
            var tex = StickerAttempt(id, keys.Localized, addrs.Localized, IconKind.Sticker, out var state);
            if (tex is not null) { LogStickerOutcomeOnce(id, addrs.Word, hit: true); _stickerStats.LocalizedHits++; return tex; }
            if (state != LoadState.Failed) return null;          // still loading (or deferred by the budget)
            _stickerLocMiss.Add(id);
            LogStickerOutcomeOnce(id, addrs.Word, hit: false);
        }
        // ...then the base bundle (same path the Chinese/base client takes).
        return StickerAttempt(id, keys.Base, addrs.Base, IconKind.Path, out _);
    }

    // One attempt = one path slot. A slot that doesn't exist yet needs a budget token (else: placeholder, try again
    // next frame — reported as Loading so the caller doesn't mistake it for a miss). Records the terminal outcome.
    private Texture2D? StickerAttempt(int id, string cacheKey, string address, IconKind kind, out LoadState state)
    {
        state = LoadState.Loading;
        int key = PathKeyFor(cacheKey);
        if (!_pathSlots.ContainsKey(key))
        {
            if (!TakeStickerStart()) { _stickerStats.Deferred++; return null; }
            _quietPathKeys.Add(key);                             // the summary replaces its per-load Info lines
        }
        var tex = LoadByPathKeyed(cacheKey, address, kind, out var uv, out state) as Texture2D;
        if (tex is not null) { _stickerReady[id] = (tex, uv); _stickerStats.Ready++; MaybeLogStickerSummary(); }
        else if (kind != IconKind.Sticker && state == LoadState.Failed && SlotGaveUp(key))
        {
            _stickerDead.Add(id);                                // bounded retries exhausted (logged by Retry.cs)
            _stickerStats.Dead++;
            MaybeLogStickerSummary();
        }
        return tex;
    }

    // Failed for good: a no-address gap, or the bounded reloads are spent (TryRetryFailedIcon stops there).
    private bool SlotGaveUp(int key)
        => _pathSlots.TryGetValue(key, out var s) && s.State == LoadState.Failed
           && (s.Path is null || s.Attempts >= MaxIconRetries);

    private bool IsQuietSlot(IconKind kind, int key)
        => (kind == IconKind.Path || kind == IconKind.Sticker) && _quietPathKeys.Contains(key);

    private StickerKeys KeysFor(int id)
    {
        if (_stickerKeys.TryGetValue(id, out var k)) return k;
        // Same cache-key strings as before ("sticker:<id>[:L|:B]"), built once per id instead of per poll.
        k = new StickerKeys("sticker:" + id, "sticker:" + id + ":L", "sticker:" + id + ":B");
        _stickerKeys[id] = k;
        return k;
    }

    private void RollStickerFrame()
    {
        int f = Time.frameCount;
        if (f == _stickerFrame) return;
        _stickerFrame = f;
        _stickerStartsThisFrame = 0;
        _stickerFrameMs = 0.0;
    }

    private bool TakeStickerStart()
    {
        RollStickerFrame();
        if (_stickerStartsThisFrame >= MaxStickerStartsPerFrame || _stickerFrameMs >= StickerFrameBudgetMs) return false;
        _stickerStartsThisFrame++;
        _stickerStats.Starts++;
        return true;
    }

    private void NoteStickerFrameCost(long t0)
    {
        HitchProbe.End("assets:sticker", HitchProbe.IsEnabled ? t0 : 0L);
        RollStickerFrame();
        _stickerFrameMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (_stickerFrameMs > _stickerStats.MaxFrameMs) _stickerStats.MaxFrameMs = _stickerFrameMs;
    }

    // Dev-facing Info at 8, 16, 32 … resolved stickers (a handful of lines per session).
    private void MaybeLogStickerSummary()
    {
        var s = _stickerStats;
        if (s.Ready + s.Dead < s.NextSummaryAt) return;
        s.NextSummaryAt *= 2;
        _log.Info($"[GameAssets][sticker] loads: ready={s.Ready} gaveUp={s.Dead} requests={s.Starts} " +
                  $"localized hit={s.LocalizedHits} miss={_stickerLocMiss.Count} deferredPolls={s.Deferred} " +
                  $"maxFrameMs={s.MaxFrameMs:0.0}");
    }

    private sealed class StickerStats
    {
        public int Ready, Dead, Starts, LocalizedHits, Deferred;
        public double MaxFrameMs;
        public int NextSummaryAt = 8;
    }

    private readonly record struct StickerKeys(string Plain, string Localized, string Base);
}
