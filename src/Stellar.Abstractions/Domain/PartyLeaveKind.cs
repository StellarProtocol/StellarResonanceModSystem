namespace Stellar.Abstractions.Domain;

/// <summary>
/// Why a party member left. Exact <c>leave_type</c> int-to-enum mapping is
/// verified at bring-up; enum stays loose to absorb new wire codes via
/// <see cref="Unknown"/>.
/// </summary>
public enum PartyLeaveKind
{
    /// <summary>Leave reason not recognised by this version of Stellar.</summary>
    Unknown      = 0,
    /// <summary>The player left voluntarily.</summary>
    Voluntary    = 1,
    /// <summary>The player was kicked by the party leader.</summary>
    Kicked       = 2,
    /// <summary>The player disconnected from the server.</summary>
    Disconnected = 3,
}
