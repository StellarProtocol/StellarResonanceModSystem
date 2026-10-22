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

    /// <summary>
    /// True when the account OWNS sticker <paramref name="configId"/> and may therefore send it — i.e. a free
    /// sticker (not unlock-gated) OR a gated one the server has synced as unlocked for this character. Mirrors the
    /// game's own <c>chat_main_vm.GetChatEmojiUnlock(emojiId)</c> predicate, queried through the framework Lua
    /// bridge. Lets a picker hide unowned gated stickers (the server rejects sending them) while keeping owned
    /// gated packs usable. A free sticker is already covered by <see cref="StickerInfo.DefaultUnlocked"/>, so a
    /// caller only needs this for the gated rows (short-circuit on <c>DefaultUnlocked</c> first).
    /// <para><b>FAIL-CLOSED.</b> Any failure — the Lua bridge not ready yet (pre-login), the chat VM not created,
    /// a nil / false / non-boolean / missing result — returns <c>false</c> (treated as not owned), so a sticker
    /// that cannot be confirmed owned is hidden rather than shown. Cheap (one Lua call); intended to be called
    /// once per gated sticker at catalog build. Main-thread only (it drives the game's Lua stack).</para>
    /// </summary>
    bool IsStickerOwned(int configId);
}
