namespace Stellar.Abstractions.Domain;

/// <summary>
/// One member's response to a dungeon ready-check, decoded from the
/// <c>WorldNtf.NotifyCaptainReady</c> push (method 71). Raised by
/// <c>IPartyEvents.ReadyCheckResponded</c> on the Unity main thread.
/// </summary>
/// <param name="CharId">The responding member's character id (wire <c>v_char_id</c>). Matches <see cref="PartyMember.CharId"/>.</param>
/// <param name="Name">The member's display name (wire <c>v_member_name</c>), or <c>null</c> if absent.</param>
/// <param name="IsReady"><c>true</c> = ready, <c>false</c> = declined (wire <c>v_ready_info.is_ready</c>).</param>
public readonly record struct ReadyCheckResponse(long CharId, string? Name, bool IsReady);
