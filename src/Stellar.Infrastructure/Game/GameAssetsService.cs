using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Infrastructure implementation of <see cref="IGameAssets"/>. Loads profession
/// (class) icons, item icons, and imagine icons via the game's own Addressables
/// loader (<c>ZResLoader</c>) and exposes them as opaque <c>UnityEngine.Texture2D</c>
/// handles so any plugin can render these icons without a hard reference to the loader type.
///
/// <para>The game stores profession icons as atlased <c>Sprite</c> assets. Each icon
/// occupies a sub-region of a larger texture sheet. The UV rect returned alongside
/// the texture handle describes that sub-region in normalised 0..1 coordinates
/// (bottom-left origin, UV texture-space convention) so callers can sample only
/// the icon's band of the atlas via a <c>SpriteElement</c> or
/// <c>MeterRowData.CrestTexture</c> / <c>CrestUv</c> pair.</para>
///
/// <para>Loading is fully asynchronous (UniTask). The service is designed to be
/// polled each frame from an Update tick — when the UniTask transitions from
/// Pending to Succeeded the result is cached and returned on all subsequent
/// calls with zero overhead. Failed or cancelled loads are marked permanently so
/// the poll short-circuits immediately.</para>
///
/// <para>Everything goes through reflection because the game's hot-update
/// assemblies (<c>Panda.*</c>, <c>ZResources</c>, <c>UniTask</c>) are loaded
/// by HybridCLR at runtime and are not available at compile time. All reflection
/// metadata is resolved once in <c>ResolveOnce()</c> and cached.</para>
///
/// <para><b>Texture lifetime:</b> loaded textures have
/// <c>HideFlags.HideAndDontSave</c> applied so they survive
/// <c>Resources.UnloadUnusedAssets()</c> on scene transitions and are not
/// included in scene serialisation. This matches the atlas lifetime in the
/// game's own asset bundles; without the flag the icon would flash once and
/// then go blank after the first scene load.</para>
/// </summary>
internal sealed partial class GameAssetsService : IGameAssets
{
    private readonly IPluginLog _log;
    private readonly IGameDataCombat _combatData;
    private readonly IGameDataResonance _resonanceData;
    private readonly IGameDataInventory _inventoryData;
    private readonly ILua _lua;   // chat-sticker ownership probe (IsStickerOwned) runs the game's own Lua predicate

    // Per-profession state. Resolved lazily on first LoadProfessionIcon(id) call.
    private readonly Dictionary<int, Slot> _slots = new();

    // Per-resonance state. Kept separate from _slots so Imagine ids never
    // collide with profession ids; both share the load machinery below.
    private readonly Dictionary<int, Slot> _imagineSlots = new();

    // Per-item state. Kept separate from other slot dictionaries so item ids
    // never collide with profession / imagine id spaces.
    private readonly Dictionary<int, Slot> _itemSlots = new();

    // Per-skill state. Kept separate from other slot dictionaries so skill ids
    // never collide with profession / imagine / item id spaces.
    private readonly Dictionary<int, Slot> _skillSlots = new();

    // Per-buff state. Kept separate from other slot dictionaries so buff ids
    // never collide with profession / imagine / item / skill id spaces.
    private readonly Dictionary<int, Slot> _buffSlots = new();

    // Per-PATH state for LoadByPath (arbitrary asset addresses with no id-based loader — e.g. dungeon
    // crests). Paths are interned to sequential ints (_pathKeys) so they reuse the int-keyed slot
    // machinery without hash collisions.
    private readonly Dictionary<int, Slot> _pathSlots = new();
    private readonly Dictionary<string, int> _pathKeys = new();

    // chat sticker config_id → its (localized, base) load addresses for the CURRENT client language. The game picks
    // a text-bearing sticker's language variant by REWRITING THE PATH (localized directory), not by a catalog remap
    // (StickerLoc.cs). Cached by id alone (language is fixed per session and folded into the addresses); only cached
    // once the language word is known, so an early call before LocalizationMgr is up re-resolves and upgrades. We
    // re-reflect only until the ChatStickersTable loads (lazy at boot); once cached, LoadStickerIcon drives the
    // localized→base attempt through LoadByPathKeyed with per-attempt slot keys (no re-reflect).
    private readonly Dictionary<int, StickerAddrs> _stickerAddrs = new();

    // Whole-catalog sticker list for the send picker (IGameAssets.GetSendableStickers). Built once via reflection
    // (BuildSendableStickers, GameAssetsService.Reflection.cs) and cached; null means "not built yet / was empty
    // last time" so we retry until the ChatStickersTable populates (lazy at boot).
    private IReadOnlyList<StickerInfo>? _sendableStickers;

    // One-shot-per-id diagnostic gate for IsStickerOwned (first ~12 distinct GATED ids evaluated). Lets us confirm
    // from one redeploy whether the ownership probe resolves (e.g. the dot-vs-colon call, or the VM/unlock_map
    // being ready at catalog-build time). Caps at 12 so a big gated set can't spam the log.
    private readonly HashSet<int> _stickerOwnedLogged = new();

    // Reflection cache. Populated in ResolveOnce(). Success is cached permanently;
    // failures retry (bounded + backoff-gated) so a boot race self-heals instead
    // of latching Failed forever.
    private bool _resolveSucceeded;
    private int _resolveAttempts;
    private int _resolveNextRetryFrame;
    private bool _resolveGaveUpLogged;

    private object? _loaderInstance;
    private MethodInfo? _loadAssetAsyncString;       // closed over Sprite (profession/item atlas icons)
    private MethodInfo? _loadAssetAsyncTexture;      // closed over Texture2D (skill/imagine icons under ui/textures/)
    private MethodInfo? _unitaskStatusGetter;        // UniTask<T>.Status (probed via Sprite return type)
    private MethodInfo? _unitaskGetAwaiter;          // UniTask<T>.GetAwaiter()
    private MethodInfo? _awaiterGetResult;           // Awaiter<T>.GetResult()
    // UniTask<T> Status/GetAwaiter/GetResult resolved per concrete UniTask type (Sprite vs Texture2D both flow here).
    private readonly Dictionary<Type, (MethodInfo Status, MethodInfo Awaiter, MethodInfo GetResult)> _uniTaskOps = new();
    private object? _cancelSourceInstance;           // ZCancelSource (rented)
    private MethodInfo? _createTokenMethod;          // ZCancelSource.CreateToken()

    public GameAssetsService(IPluginLog log, IGameDataCombat combatData, IGameDataResonance resonanceData, IGameDataInventory inventoryData, ILua lua)
    {
        _log = log;
        _combatData = combatData;
        _resonanceData = resonanceData;
        _inventoryData = inventoryData;
        _lua = lua;
    }

    /// <inheritdoc/>
    public object? LoadProfessionIcon(int professionId)
        => LoadProfessionIcon(professionId, out _);

    /// <inheritdoc/>
    public object? LoadProfessionIcon(int professionId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (professionId <= 0) return null;
        if (_slots.ContainsKey(professionId))                       // existing slot: don't re-hit the table
            return LoadIcon(_slots, professionId, address: null, IconKind.Profession, out uv);
        var prof = _combatData.GetProfession(professionId);
        if (prof is null) return null;                              // table not ready → poll again, NO Failed slot
        return LoadIcon(_slots, professionId, prof.Value.IconPath, IconKind.Profession, out uv);
    }

    /// <inheritdoc/>
    public object? LoadImagineIcon(int skillId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (skillId <= 0) return null;
        var address = _resonanceData.GetImagineForSkill(skillId)?.IconAddress;
        // Skill icons live under ui/textures/ as standalone Texture2D (not atlas Sprites) — load as Texture2D.
        return LoadIcon(_imagineSlots, skillId, address, IconKind.Imagine, out uv);
    }

    /// <inheritdoc/>
    public object? LoadItemIcon(int itemId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (itemId <= 0) return null;

        // Slot-first: the per-frame cache-hit path (11 gear cards × every frame) must not pay the
        // item-table lookup; GetItem only runs when the slot doesn't exist yet (perf review).
        if (_itemSlots.ContainsKey(itemId))
            return LoadIcon(_itemSlots, itemId, address: null, IconKind.Item, out uv);

        // If the item table hasn't populated yet, return null without creating a
        // slot — a per-frame dict miss is free and the caller will poll again.
        // Only when the row exists do we enter the slot machinery; a missing
        // IconPath on a known row is memoized Failed (logged once there).
        var row = _inventoryData.GetItem(itemId);
        if (row is null) return null;

        // Item icons use the raw IconPath from ItemTableBase.Icon — same raw-address
        // convention as profession icons (no directory prefix). Loads as Texture2D
        // (live-verified); a failure retries once as Sprite.
        return LoadIcon(_itemSlots, itemId, row.Value.IconPath, IconKind.Item, out uv);
    }

    /// <inheritdoc/>
    public object? LoadSkillIcon(int skillId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (skillId <= 0) return null;

        // Slot-first: the per-frame cache-hit path must not pay the skill-table lookup.
        if (_skillSlots.ContainsKey(skillId))
            return LoadIcon(_skillSlots, skillId, address: null, IconKind.Skill, out uv);

        // If the skill table hasn't populated yet, return null without creating a
        // slot — a per-frame dict miss is free and the caller will poll again.
        var row = _combatData.GetSkill(skillId);
        if (row is null) return null;

        // Skill icons live under ui/textures/ as standalone Texture2D (same family as Imagine).
        return LoadIcon(_skillSlots, skillId, row.Value.IconPath, IconKind.Skill, out uv);
    }

    /// <inheritdoc/>
    public object? LoadBuffIcon(int buffId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (buffId <= 0) return null;

        // Slot-first: the per-frame cache-hit path must not pay the buff-table lookup.
        if (_buffSlots.ContainsKey(buffId))
            return LoadIcon(_buffSlots, buffId, address: null, IconKind.Buff, out uv);

        // If the buff table hasn't populated yet, return null without creating a
        // slot — a per-frame dict miss is free and the caller will poll again.
        var row = _combatData.GetBuff(buffId);
        if (row is null) return null;

        // Buff icons follow the same path convention as skill icons (Texture2D family).
        return LoadIcon(_buffSlots, buffId, row.Value.IconPath, IconKind.Buff, out uv);
    }

    /// <inheritdoc/>
    public object? LoadByPath(string assetPath, out UvRect uv) => LoadByPathKeyed(assetPath, assetPath, IconKind.Path, out uv, out _);

    // Shared path-load core. cacheKey distinguishes slots; loadAddress is what reaches the loader; kind selects the
    // loader/retry behaviour; state reports the slot's state AFTER this call (so a caller can tell "still loading"
    // from "failed"). For every normal caller (and the public LoadByPath) cacheKey == loadAddress and kind == Path —
    // byte-identical to before. Stickers pass per-attempt cacheKeys with already-localized/base addresses (and
    // IconKind.Sticker for the localized attempt) so each language variant gets its own slot and a localized miss
    // surfaces as Failed for the base fallback (LoadStickerIcon).
    private object? LoadByPathKeyed(string cacheKey, string loadAddress, IconKind kind, out UvRect uv, out LoadState state)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        state = LoadState.Failed;
        if (string.IsNullOrEmpty(loadAddress)) return null;
        var key = PathKeyFor(cacheKey);
        var tex = _pathSlots.ContainsKey(key)                    // existing slot: don't re-pass the address
            ? LoadIcon(_pathSlots, key, address: null, kind, out uv)
            : LoadIcon(_pathSlots, key, loadAddress, kind, out uv);
        if (_pathSlots.TryGetValue(key, out var slot)) state = slot.State;
        return tex;
    }

    // Stable per-cacheKey int (no hash collisions) — interned on first use.
    private int PathKeyFor(string cacheKey)
    {
        if (_pathKeys.TryGetValue(cacheKey, out var key)) return key;
        key = _pathKeys.Count + 1;
        _pathKeys[cacheKey] = key;
        return key;
    }

    /// <inheritdoc/>
    public IReadOnlyList<StickerInfo> GetSendableStickers()
    {
        if (_sendableStickers is { Count: > 0 }) return _sendableStickers;
        // Build (reflection, GameAssetsService.Reflection.cs). The table loads lazily at boot, so an early call
        // returns empty — do NOT cache an empty result, so a later call rebuilds until the table populates.
        var built = BuildSendableStickers();
        if (built.Count > 0) _sendableStickers = built;
        return built;
    }

    /// <inheritdoc/>
    public bool IsStickerOwned(int configId)
    {
        if (configId <= 0) return false;
        // FAIL-CLOSED: a Lua bridge that isn't up yet (pre-login) → not owned (hide it). Gating on Ready FIRST also
        // guarantees the sentinel-clear below actually runs, so we never read a stale `true` left by a previous
        // sticker when DoString would have no-op'd.
        if (!_lua.Ready) { LogOwnedOnce(configId, ready: false, result: false); return false; }

        // ILua has no "call a function and return its value" primitive, so park-and-read (Framework-Interop §2):
        //   • clear the sentinel FIRST, OUTSIDE the pcall — so a failed/errored/absent call leaves it nil (false),
        //     never a stale value from the previous sticker;
        //   • GetChatEmojiUnlock is a DOT function (chat_main_vm.lua:2434 — first arg is emojiId, NO implicit self),
        //     and the game's own callers invoke it with a dot (chat_emoji_item.lua:58 vm.GetChatEmojiUnlock(id)).
        //     A COLON would pass the VM table as self→emojiId and shift the real id to an ignored arg #2 → false for
        //     every id (hides all gated stickers). So call it with a DOT. (GetVM stays a dot too.)
        //   • it returns a Lua boolean; `<result> and true or nil` collapses false/nil → nil (the global is removed)
        //     and true → true, so the type-STRICT TryReadGlobalBool only ever sees true-or-absent (a Lua number/false
        //     can never surface and be misread).
        _lua.DoString(
            "rawset(_G,'__stellar_sticker_owned',nil)\n" +
            "pcall(function() rawset(_G,'__stellar_sticker_owned', (Z.VMMgr.GetVM('chat_main').GetChatEmojiUnlock("
            + configId + ")) and true or nil) end)");
        bool owned = _lua.TryReadGlobalBool("__stellar_sticker_owned", out var v) && v;
        LogOwnedOnce(configId, ready: true, result: owned);
        return owned;
    }

    // One-shot-per-id (capped at 12) developer diagnostic for the ownership probe — English, Info level. Dedups by
    // configId and stops after 12 distinct ids so a large gated set can't spam the log.
    private void LogOwnedOnce(int configId, bool ready, bool result)
    {
        if (_stickerOwnedLogged.Count >= 12 || !_stickerOwnedLogged.Add(configId)) return;
        _log.Info($"[GameAssets][sticker] owned? id={configId} ready={ready} result={result}");
    }

    // Address-agnostic slot machinery shared by all icon kinds. The caller
    // resolves the ZResLoader address from its own data source and passes it in;
    // null/empty address fails the slot (full-rect uv, null texture). The slot
    // dictionary is keyed by the caller's id space so the kinds never collide.
    private Texture2D? LoadIcon(Dictionary<int, Slot> slots, int key, string? address, IconKind kind, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (!slots.TryGetValue(key, out var slot))
        {
            if (string.IsNullOrEmpty(address))
            {
                var label = LabelOf(kind);
                _log.Warning($"[GameAssets][icon] {label}={key} has no IconPath");
                slot = new Slot { State = LoadState.Failed };
            }
            else
            {
                slot = BeginLoad(address!, key, kind);
            }
            slots[key] = slot;
        }

        if (slot.State == LoadState.Loaded)
        {
            uv = slot.Uv;
            return slot.Texture;
        }
        if (slot.State != LoadState.Loading)
        {
            if (slot.State == LoadState.Failed) TryRetryFailedIcon(slots, key, slot, kind);
            return null;
        }

        // Still loading — poll the UniTask status.
        var tex = PollLoadingSlot(slots, slot, key, kind);
        uv = slots.TryGetValue(key, out var updated) ? updated.Uv : uv;
        return tex;
    }

    // Atlas Sprite (profession crest) → (Texture2D, normalised UV sub-rect).
    private void ResolveSprite(Slot slot, int key, string label, Sprite sprite, bool quiet = false)
    {
        var tex = sprite.texture;
        if (tex is null)
        {
            slot.State = LoadState.Failed;
            slot.UniTask = null;
            _log.Warning($"[GameAssets][icon] loaded {label}={key} Sprite has no texture");
            return;
        }
        // HideAndDontSave so the texture survives Resources.UnloadUnusedAssets() on scene transitions.
        tex.hideFlags = HideFlags.HideAndDontSave;
        var r = sprite.textureRect;
        float tw = tex.width, th = tex.height;
        // textureRect is pixels, bottom-left origin; UvRect is normalised bottom-left — straight normalise, no y-flip.
        slot.Uv = new UvRect(r.x / tw, r.y / th, r.width / tw, r.height / th);
        slot.Texture = tex;
        slot.State = LoadState.Loaded;
        slot.UniTask = null;
        if (!quiet) _log.Info($"[GameAssets][icon] loaded {label}={key} sprite='{sprite.name}' atlas={tex.width}x{tex.height} rect=({r.x},{r.y},{r.width},{r.height})");
    }

    // Address-agnostic load core: kick off LoadAssetAsync for an already-resolved
    // ZResLoader address. Callers resolve the address from their own data source.
    private Slot BeginLoad(string address, int key, IconKind kind)
    {
        var label = LabelOf(kind);
        if (!ResolveOnce())
        {
            return new Slot { State = LoadState.Failed };
        }

        if (!MintCancelToken(key, label, out var token))
        {
            return new Slot { State = LoadState.Failed };
        }

        try
        {
            // loader.LoadAssetAsync<Sprite|Texture2D>(addr, token, 0, false) -> UniTask<T>. Texture2D loads go through
            // the ZResAddress overload → convert the address via BuildTextureLoadAddress (StickerLoc.cs). The address
            // is passed as-is; for stickers the per-language choice is already baked into the address STRING (the
            // loader does NOT remap it). Sprite loads stay on the string overload — byte-identical for every plugin.
            var isTexture = IsTexture(kind);
            var method = isTexture ? _loadAssetAsyncTexture! : _loadAssetAsyncString!;
            var loadAddr = isTexture ? BuildTextureLoadAddress(address) : address;
            // allowFail=TRUE only for the localized sticker probe (an expected miss for base-only stickers): with false
            // the game Debug.LogError's "fail, asset not exist" + a stack trace, synchronously, per miss. It gates only
            // that log — same lookups, still Succeeded(null) (StickerLoad.cs). Every other load keeps false.
            var allowFail = kind == IconKind.Sticker;
            var unitask = method.Invoke(_loaderInstance, new object[] { loadAddr, token, 0, allowFail });
            if (unitask is null)
            {
                _log.Warning($"[GameAssets][icon] LoadAssetAsync returned null for {label}={key} path='{address}'");
                return new Slot { State = LoadState.Failed };
            }
            if (!IsQuietSlot(kind, key)) _log.Info($"[GameAssets][icon] requested {label}={key} path='{address}'");
            return new Slot { State = LoadState.Loading, UniTask = unitask, Path = address };
        }
        catch (Exception ex)
        {
            // TargetInvocationException unwraps to the real cause; log both.
            var inner = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            _log.Warning(
                $"[GameAssets][icon] LoadAssetAsync threw for {label}={key} path='{address}': " +
                $"{inner.GetType().Name}: {inner.Message}");
            return new Slot { State = LoadState.Failed };
        }
    }


    private enum LoadState
    {
        Loading,
        Loaded,
        Failed,
    }

    // Encodes both the log label and the initial loader choice for BeginLoad.
    private enum IconKind
    {
        Profession, // Sprite atlas — loads as Sprite
        Imagine,    // standalone Texture2D — loads as Texture2D
        Item,       // standalone Texture2D (in-world: all 11 equipped items resolved as Texture2D,
                    // Sprite-first produced 11 warnings/login) — Sprite is the one-shot fallback
        Skill,      // standalone Texture2D — skill icons live under ui/textures/ like Imagine
        Buff,       // atlas Sprite — buff icon paths are under ui/atlas/ (Sprite atlas, same family as Profession)
        Path,       // LoadByPath: arbitrary address, type unknown → Texture2D first, Sprite fallback (like Item)
        Sticker,    // chat sticker LOCALIZED attempt: Texture2D only, NO Sprite retry + NO bounded reload so a
                    // base-only sticker's localized miss surfaces as Failed at once → base fallback (LoadStickerIcon)
    }

    private static bool IsTexture(IconKind kind) => kind is IconKind.Imagine or IconKind.Item or IconKind.Skill or IconKind.Path or IconKind.Sticker;
    private static string LabelOf(IconKind kind) => kind switch
    {
        IconKind.Profession => "profession",
        IconKind.Imagine    => "imagine",
        IconKind.Item       => "item",
        IconKind.Skill      => "skill",
        IconKind.Buff       => "buff",
        IconKind.Path       => "path",
        IconKind.Sticker    => "sticker",
        _                   => "icon",
    };

    // Mutable per-icon state. Class (not struct) so the polling path can
    // update fields in-place without re-inserting the dictionary entry each
    // time (we re-insert anyway for clarity, but the class lets us mutate
    // before the re-insert without struct-copy surprises).
    private sealed class Slot
    {
        public LoadState State;
        public object? UniTask;
        public Texture2D? Texture;
        public UvRect Uv = new UvRect(0f, 0f, 1f, 1f);
        public string? Path;
        // Set after the one-shot Texture2D retry for Item slots so the fallback
        // cannot loop: once true, no further retry is attempted regardless of outcome.
        public bool RetriedAlternate;
        public int Attempts;               // bounded-retry counter for load faults (NOT the item Sprite one-shot)
        public int NextRetryFrame;         // Time.frameCount gate for the next retry attempt
        public bool RetryExhaustedLogged;  // so the give-up message logs exactly once
    }
}
