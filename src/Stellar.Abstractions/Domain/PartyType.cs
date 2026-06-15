namespace Stellar.Abstractions.Domain;

/// <summary>Party size category. Matches <c>Zproto.ETeamMemberType</c>.</summary>
public enum PartyType
{
    /// <summary>Not currently in a party.</summary>
    Solo     = -1,

    /// <summary>Regular 5-person party. Matches <c>ETeamMemberTypeFive</c>.</summary>
    Regular5 = 0,

    /// <summary>20-person raid. Matches <c>ETeamMemberTypeTwenty</c>.</summary>
    Raid20   = 1,
}
