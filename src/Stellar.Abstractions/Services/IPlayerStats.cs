namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only access to live player attribute values. Each tracked attribute is
/// identified by its <c>Zproto.EAttrType</c> integer code (e.g. 11011 for
/// <c>AttrStrengthTotal</c>). Pair with <see cref="IGameDataCombat.GetAttribute"/>
/// to resolve the localized label and
/// <see cref="IGameDataCombat.GetAttributeProfile"/> to discover the UI group.
///
/// Attributes must be <see cref="Subscribe"/>d before they are sampled. The
/// probe polls only subscribed IDs each <c>Game.Update</c> tick — sampling all
/// 1289 EAttrType members per frame would be wasteful. Unsubscribed IDs return
/// null.
/// </summary>
public interface IPlayerStats
{
    /// <summary>True once a character is loaded and the entity probe is bootstrapped.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns the latest sampled value for <paramref name="attrId"/>, or null
    /// if the ID is not subscribed or the player isn't loaded. Value type is
    /// long regardless of storage size — the probe handles int/long
    /// memoization internally.
    /// </summary>
    long? TryGetAttribute(int attrId);

    /// <summary>Adds an attribute ID to the polled set. Idempotent.</summary>
    void Subscribe(int attrId);

    /// <summary>Removes an attribute ID from the polled set.</summary>
    void Unsubscribe(int attrId);
}
