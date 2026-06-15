using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Toolkit service for loading game assets (textures, sprites) via the game's own
/// Addressables loader (<c>ZResLoader</c>). Provides an opaque texture handle so
/// plugins can render icons without a direct Unity dependency on <c>Texture2D</c>.
/// </summary>
/// <remarks>
/// Loading is asynchronous (driven by the game's UniTask Addressables pipeline).
/// Call <see cref="LoadProfessionIcon(int)"/> every frame from an Update tick; it
/// returns <c>null</c> while the load is in progress and the resolved texture
/// handle once the load succeeds. A permanently failed load also returns
/// <c>null</c> — the caller should fall back to text-only rendering.
/// </remarks>
public interface IGameAssets
{
    /// <summary>
    /// Returns the atlas texture handle for the profession icon identified by
    /// <paramref name="professionId"/>, kicking off an async Addressables load on
    /// the first call. Returns <c>null</c> while loading, on failure, or when no
    /// profession row / icon path is available for the given id.
    /// </summary>
    /// <param name="professionId">The numeric profession id (1–11 for known classes).</param>
    /// <returns>
    /// An opaque <c>UnityEngine.Texture2D</c> handle (safe to cast to
    /// <c>UnityEngine.Texture</c> for rendering) or <c>null</c>. The UV
    /// sub-rect for the icon's region within the atlas is returned via
    /// <see cref="LoadProfessionIcon(int, out UvRect)"/>.
    /// </returns>
    object? LoadProfessionIcon(int professionId);

    /// <summary>
    /// Same as <see cref="LoadProfessionIcon(int)"/> but also returns the
    /// normalised UV rect (<see cref="UvRect"/> — 0..1 range, bottom-left
    /// origin, UV texture-space convention) for the icon's sub-region within
    /// the atlas. Pass the handle and UV to a <see cref="SpriteElement"/> or
    /// <c>MeterRowData.CrestTexture</c> / <c>CrestUv</c> pair.
    /// </summary>
    /// <param name="professionId">The numeric profession id (1–11 for known classes).</param>
    /// <param name="uv">
    /// The UV sub-rect for the icon. Set to <c>(0,0,1,1)</c> (full texture) when
    /// the texture is not yet available or the load failed.
    /// </param>
    object? LoadProfessionIcon(int professionId, out UvRect uv);

    /// <summary>Atlas/texture handle for a Battle Imagine card icon, async-loaded via ZResLoader.
    /// Returns null while loading or when no icon is available; <paramref name="uv"/> is the icon's sub-rect.</summary>
    /// <param name="skillId">The Battle Imagine skill id (from the entity's equipped skill loadout).</param>
    /// <param name="uv">
    /// The UV sub-rect for the icon. Set to <c>(0,0,1,1)</c> (full texture) when
    /// the texture is not yet available or the load failed.
    /// </param>
    object? LoadImagineIcon(int skillId, out UvRect uv);

    /// <summary>Atlas/texture handle for an item's inventory icon, async-loaded via ZResLoader using
    /// the item row's icon path. Returns null while loading, on failure, or when the item/icon is
    /// unknown; <paramref name="uv"/> is the icon's sub-rect ((0,0,1,1) until resolved).</summary>
    /// <param name="itemId">The item id (gear pieces share ids with their <c>ItemTable</c> rows).</param>
    /// <param name="uv">The UV sub-rect for the icon within its atlas, or full-rect when standalone.</param>
    object? LoadItemIcon(int itemId, out UvRect uv);

    /// <summary>Atlas/texture handle for a skill's icon, async-loaded via ZResLoader using the
    /// skill row's icon path. Returns null while loading, on failure, or when the skill/icon is
    /// unknown; <paramref name="uv"/> is the icon's sub-rect ((0,0,1,1) until resolved).</summary>
    /// <param name="skillId">The skill id (from the entity's skill loadout).</param>
    /// <param name="uv">The UV sub-rect for the icon within its atlas, or full-rect when standalone.</param>
    object? LoadSkillIcon(int skillId, out UvRect uv);

    /// <summary>Atlas/texture handle for a buff/debuff's icon, async-loaded via ZResLoader using the buff
    /// row's icon path. Returns null while loading, on failure, or when the buff/icon is unknown;
    /// <paramref name="uv"/> is the icon's sub-rect ((0,0,1,1) until resolved).</summary>
    /// <param name="buffId">The buff base id (BuffTable row id).</param>
    /// <param name="uv">The UV sub-rect for the icon within its atlas, or full-rect when standalone.</param>
    object? LoadBuffIcon(int buffId, out UvRect uv);
}
