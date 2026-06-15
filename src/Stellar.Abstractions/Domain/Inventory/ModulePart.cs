namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>
/// A single attribute contribution from a module. A module typically has
/// 1–3 parts (see <c>lua/ui/model/mod_define.lua: ModEffectMaxCount = 3</c>).
/// </summary>
/// <param name="AttrId">EAttrType integer code — same namespace as
/// <c>IPlayerStats</c> subscriptions and
/// <c>IGameDataCombat.GetAttributeProfile</c>.</param>
/// <param name="Name">Localized attribute name (resolved via
/// <c>IGameDataCombat.GetAttribute</c> or <c>GetAttributeProfile</c>).</param>
/// <param name="Value">Raw integer value — units depend on attribute
/// (see Phase 6 percent-scale handling).</param>
public sealed record ModulePart(int AttrId, string Name, int Value);
