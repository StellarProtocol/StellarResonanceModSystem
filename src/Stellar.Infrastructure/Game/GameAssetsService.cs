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

    // Reflection cache. Populated in ResolveOnce() — null after a failed
    // resolution attempt so we don't keep paying the lookup cost.
    private bool _resolveAttempted;
    private bool _resolveSucceeded;

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

    public GameAssetsService(IPluginLog log, IGameDataCombat combatData, IGameDataResonance resonanceData, IGameDataInventory inventoryData)
    {
        _log = log;
        _combatData = combatData;
        _resonanceData = resonanceData;
        _inventoryData = inventoryData;
    }

    /// <inheritdoc/>
    public object? LoadProfessionIcon(int professionId)
        => LoadProfessionIcon(professionId, out _);

    /// <inheritdoc/>
    public object? LoadProfessionIcon(int professionId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (professionId <= 0) return null;
        var address = _combatData.GetProfession(professionId)?.IconPath;
        return LoadIcon(_slots, professionId, address, IconKind.Profession, out uv);
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
        if (slot.State != LoadState.Loading) return null;

        // Still loading — poll the UniTask status.
        var tex = PollLoadingSlot(slots, slot, key, kind);
        uv = slots.TryGetValue(key, out var updated) ? updated.Uv : uv;
        return tex;
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

    // One-shot fallback: if this is a first-failure on an Item slot, retry with
    // the SPRITE loader (items load Texture2D-first per live evidence; the retry covers a
    // hypothetical atlased item icon). Sets RetriedAlternate so it can't loop.
    // Always writes the final slot state into slots[key].
    private void RetryItemAlternate(Dictionary<int, Slot> slots, Slot slot, int key, IconKind kind, string reason)
    {
        if (kind != IconKind.Item || slot.RetriedAlternate || slot.Path is null)
        {
            slots[key] = slot;
            return;
        }

        _log.Info($"[GameAssets][icon] item={key} {reason} — retrying as Sprite");
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

    // Atlas Sprite (profession crest) → (Texture2D, normalised UV sub-rect).
    private void ResolveSprite(Slot slot, int key, string label, Sprite sprite)
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
        _log.Info($"[GameAssets][icon] loaded {label}={key} sprite='{sprite.name}' atlas={tex.width}x{tex.height} rect=({r.x},{r.y},{r.width},{r.height})");
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
            // loader.LoadAssetAsync<Sprite|Texture2D>(address, token, 0, false) -> UniTask<T>
            var method = IsTexture(kind) ? _loadAssetAsyncTexture! : _loadAssetAsyncString!;
            var unitask = method.Invoke(_loaderInstance, new object[] { address, token, 0, false });
            if (unitask is null)
            {
                _log.Warning($"[GameAssets][icon] LoadAssetAsync returned null for {label}={key} path='{address}'");
                return new Slot { State = LoadState.Failed };
            }
            _log.Info($"[GameAssets][icon] requested {label}={key} path='{address}'");
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
    }

    private static bool IsTexture(IconKind kind) => kind is IconKind.Imagine or IconKind.Item or IconKind.Skill;
    private static string LabelOf(IconKind kind) => kind switch
    {
        IconKind.Profession => "profession",
        IconKind.Imagine    => "imagine",
        IconKind.Item       => "item",
        IconKind.Skill      => "skill",
        IconKind.Buff       => "buff",
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
    }
}
