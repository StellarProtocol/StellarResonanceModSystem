using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Identity concern of <see cref="PandaPlayerStateProbe"/>. The entity-attribute
/// path in the sibling partials answers "what is the player's live state"; this
/// answers "who is the player", from a source that does not go dark when the
/// world entity's attribute bag is empty. See
/// <see cref="PandaCharIdentityReader"/> for the record chain and
/// <c>docs/recon/playerstate-probe-mounted-blackout.md</c> for the repro.
/// </summary>
internal sealed partial class PandaPlayerStateProbe
{
    public bool TryReadIdentity(out PlayerIdentitySnapshot identity)
    {
        identity = default;

        // Null when Host wired no char-record source (visual-scenario / test
        // builds). Identity then simply stays unavailable and every consumer
        // behaves exactly as it did before this path existed.
        if (_charIdentityReader is null)
        {
            return false;
        }

        if (!_charIdentityReader.TryRead(out var record))
        {
            return false;
        }

        identity = new PlayerIdentitySnapshot
        {
            CharId = record.CharId,
            Name = record.Name,
            Level = record.Level,
            Profession = record.Profession,
        };
        return true;
    }
}
