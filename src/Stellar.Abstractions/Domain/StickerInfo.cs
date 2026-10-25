namespace Stellar.Abstractions.Domain;

/// <summary>
/// One chat picture-emoji / sticker catalog row (from the game's <c>ChatStickersTable</c>), exposed to plugins
/// by <see cref="Stellar.Abstractions.Services.IGameAssets.GetSendableStickers"/> for building a send picker.
/// Load its thumbnail with <see cref="Stellar.Abstractions.Services.IGameAssets.LoadStickerIcon"/> and send it
/// with <see cref="Stellar.Abstractions.Services.IChat.SendSticker"/> — both keyed by <see cref="ConfigId"/>.
/// </summary>
/// <param name="ConfigId">The sticker config_id (the <c>ChatPictureEmoji.config_id</c> the wire carries).</param>
/// <param name="Type">
/// The <c>EChatStickersType</c> category: 1 = StandardEmoji, 2 = Emoji, 3 = Picture, 4 = HeadPicture,
/// 5 = QuickMessage (canned text — not a real image).
/// </param>
/// <param name="GroupId">The sticker's group id (sub-categorisation within a type).</param>
/// <param name="DefaultUnlocked">
/// True when the sticker is freely sendable (NOT unlock-gated). The framework has already resolved the game's
/// <c>!(IsDefUnlock&gt;0 &amp;&amp; UnlockItem&gt;0)</c> gating quirk, so a picker filters on this flag directly —
/// a gated sticker the player does not own would be rejected server-side.
/// </param>
/// <param name="Res">The resource / sprite name (the <c>ChatStickersTable.Res</c> column).</param>
public sealed record StickerInfo(int ConfigId, int Type, int GroupId, bool DefaultUnlocked, string Res);
