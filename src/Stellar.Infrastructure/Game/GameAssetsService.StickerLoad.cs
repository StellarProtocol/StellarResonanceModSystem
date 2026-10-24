using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Chat-sticker thumbnail loading for <see cref="GameAssetsService"/> (split out of GameAssetsService.cs for the
/// 500-LoC gate — a pure move). Address building + per-language localization live in GameAssetsService.StickerLoc.cs.
/// </summary>
internal sealed partial class GameAssetsService
{
    /// <inheritdoc/>
    public object? LoadStickerIcon(int stickerConfigId, out UvRect uv)
    {
        uv = new UvRect(0f, 0f, 1f, 1f);
        if (stickerConfigId <= 0) return null;

        // Resolve + cache the (localized, base) addresses for the current client language. Cache ONLY once the
        // language word is known (wordReady) — an early call before LocalizationMgr is up resolves a base-only shape
        // without caching, so a later frame upgrades to the localized address (StickerLoc.cs).
        if (!_stickerAddrs.TryGetValue(stickerConfigId, out var addrs))
        {
            var res = TryResolveStickerRes(stickerConfigId);     // table not ready / unknown id / empty Res → retry
            if (string.IsNullOrEmpty(res)) return null;          // null WITHOUT caching, like the id-based loaders
            addrs = BuildStickerAddresses(res!, out var wordReady);
            if (wordReady) _stickerAddrs[stickerConfigId] = addrs;
        }

        // Simplified-Chinese / base client (or language not up yet): load the unprefixed base bundle, exactly as
        // before (Path kind keeps the Texture2D-first + Sprite fallback for any atlased sticker).
        if (!addrs.HasLocalized)
            return LoadByPathKeyed($"sticker:{stickerConfigId}", addrs.Base, IconKind.Path, out uv, out _);

        // Non-Chinese client: try the per-language localized bundle first. IconKind.Sticker fails fast (no Sprite
        // retry, no bounded reload) so a non-text sticker that ships base-only surfaces as Failed immediately...
        var tex = LoadByPathKeyed($"sticker:{stickerConfigId}:L", addrs.Localized, IconKind.Sticker, out uv, out var state);
        if (tex is not null) { LogStickerOutcomeOnce(stickerConfigId, addrs.Word, hit: true); return tex; }
        if (state != LoadState.Failed) return null;              // still loading the localized bundle

        // ...then fall back to the base bundle (same path the Chinese/base client takes).
        LogStickerOutcomeOnce(stickerConfigId, addrs.Word, hit: false);
        return LoadByPathKeyed($"sticker:{stickerConfigId}:B", addrs.Base, IconKind.Path, out uv, out _);
    }
}
