using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Chat-sticker CATALOG concern of <see cref="IGameAssets"/> (which inherits this). Split out so
/// <see cref="IGameAssets"/> stays within the framework's interface-width budget while keeping the whole surface
/// reachable through a single <c>_services.GameAssets</c> handle — the sticker THUMBNAIL loader
/// (<see cref="IGameAssets.LoadStickerIcon"/>) stays on <see cref="IGameAssets"/>; the catalog ENUMERATION lives
/// here.
/// </summary>
public interface IGameStickerCatalog
{
    /// <summary>
    /// Enumerates the game's chat picture-emoji / sticker catalog (<c>ChatStickersTable</c>) for building a send
    /// picker. Returns ALL rows — both freely-sendable and unlock-gated — each tagged with
    /// <see cref="StickerInfo.DefaultUnlocked"/> (the framework resolves the gating quirk); the caller filters.
    /// The list is built once by reflection and cached (the table is a static singleton); an empty result (the
    /// table hasn't loaded yet — it loads lazily at boot) is NOT cached, so a later call rebuilds until it
    /// populates. Safe to call every frame.
    /// </summary>
    IReadOnlyList<StickerInfo> GetSendableStickers();
}
