using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Wardrobe-preview options added in framework 2.31.0: the weapon on/off switch and the outfit's own show/hide state.
/// Split out of <see cref="IWardrobePreview"/>, which is at the framework's interface-size budget; consumers reach both
/// through <c>services.WardrobePreview</c> exactly as if they were declared there. A plugin that uses either needs
/// framework ≥ 2.31.0.
/// </summary>
public interface IWardrobePreviewOptions
{
    /// <summary>When false (default) the preview hides the model's weapon; true draws it — the outfit's saved weapon
    /// skin (<see cref="WardrobeRegions.WeaponSkinPreview"/>) when the outfit map carries one, else the weapon the
    /// player has on now. Applies live to the shown model (no rebuild). Global to the preview service — shared by every
    /// plugin that uses it — so set it before each <c>Show</c>.</summary>
    bool ShowWeapon { get; set; }

    /// <summary>Same as <see cref="IWardrobePreview.Show"/>, plus the outfit's OWN show/hide state. Without it the
    /// preview model inherits the player's CURRENT global "hide this piece" setting, so a piece hidden right now can't be
    /// previewed even when the outfit shows it.</summary>
    /// <param name="self">The local player entity (its outfit is overridden by <paramref name="outfit"/>).</param>
    /// <param name="outfit">Region→fashionId map to dress the model with.</param>
    /// <param name="dyes">Optional per-region, per-area dye colours (as in <see cref="IWardrobePreview.Show"/>).</param>
    /// <param name="hidden">Region→hidden (<c>true</c> = hidden) over the 14 <see cref="WardrobeRegions.All"/>
    /// regions. A region missing from the map is SHOWN; keys outside <see cref="WardrobeRegions.All"/> (e.g. the
    /// weapon skin) are ignored. Null = the player's current global setting applies (identical to
    /// <see cref="IWardrobePreview.Show"/>).</param>
    void Show(EntityId self, IReadOnlyDictionary<int, int> outfit,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, float[]>>? dyes,
        IReadOnlyDictionary<int, bool>? hidden);
}
