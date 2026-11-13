using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

/// <summary>A player's online state as carried by <c>SocialData.basic_data</c> (<c>zproto.BasicData</c>).
/// <para>The game's own friends list treats <c>offline_time == 0</c> as online and any other value as the moment the
/// player went offline; <see cref="IsOnline"/> follows that rule. proto3 omits zero values, so an online player's
/// reply simply has no <c>offline_time</c> field.</para></summary>
/// <param name="OfflineTimeMs">When the player went offline, Unix ms (<c>basic_data.offline_time</c>, field 9);
/// 0 while online. Compare against <see cref="ICombatSnapshot.ServerNowMs"/> for "offline for N minutes".</param>
/// <param name="OnlineTimeMs">When the player last came online, Unix ms (<c>basic_data.online_time</c>, field 16);
/// 0 when the server didn't send it.</param>
/// <param name="PersonalStates">Status ids the player has set (<c>basic_data.personal_state</c>, field 8), in wire
/// order; never null, empty when none. Ids index the game's <c>ChatStatusTable</c> (e.g. 1 = online, 6 = offline,
/// 2 = in a dungeon); when several are set the game's friends list shows the one with the highest table priority.</param>
/// <param name="ReceivedAtMs">Framework server clock (<see cref="ICombatSnapshot.ServerNowMs"/> domain, Unix ms)
/// when the reply carrying this state arrived; 0 when it arrived before the first server-time sync.
/// Stamped by the framework cache, not the wire.</param>
public readonly record struct SocialPresence(
    long OfflineTimeMs, long OnlineTimeMs, IReadOnlyList<int> PersonalStates, long ReceivedAtMs = 0)
{
    /// <summary>True when <see cref="OfflineTimeMs"/> is 0 — the game's own online test.</summary>
    public bool IsOnline => OfflineTimeMs == 0;

    // Explicit backing field so a `default` instance (field initializers don't run) still reports empty, not null.
    private readonly IReadOnlyList<int>? _personalStates = PersonalStates;

    /// <summary>Status ids, never null (a <c>default</c> instance reports an empty list).</summary>
    public IReadOnlyList<int> PersonalStates
    {
        get => _personalStates ?? Array.Empty<int>();
        init => _personalStates = value;
    }
}

/// <summary>Which top-level <c>SocialData</c> sections a <see cref="SocialSnapshot"/> holds data from. The game asks
/// the server for a subset of sections per request (an ID card asks for all; nameplates and portraits ask for a few),
/// and a reply simply omits the sections it wasn't asked for.
/// <para>On a decoded reply these are the sections that reply carried. On a snapshot read from the framework cache
/// they are the sections the snapshot has data for: the cache keeps a section from an earlier reply when a later one
/// doesn't carry it. <see cref="None"/> means "not reported" (a snapshot built by hand rather than decoded).</para>
/// <para>These flags are the framework's own numbering, not the game's request-mask bits.</para></summary>
[Flags]
public enum SocialSections
{
    /// <summary>Not reported.</summary>
    None = 0,
    /// <summary><c>basic_data</c> (field 3): <see cref="SocialSnapshot.Name"/>, <see cref="SocialSnapshot.Level"/>,
    /// <see cref="SocialSnapshot.Presence"/>.</summary>
    Basic = 1 << 0,
    /// <summary><c>avatar_info</c> (field 4): <see cref="SocialSnapshot.ProfileUrl"/>, <see cref="SocialSnapshot.HalfBodyUrl"/>.</summary>
    Avatar = 1 << 1,
    /// <summary><c>profession_data</c> (field 6): <see cref="SocialSnapshot.ProfessionId"/>.</summary>
    Profession = 1 << 2,
    /// <summary><c>equip_data</c> (field 7): <see cref="SocialSnapshot.Gear"/>.</summary>
    Equip = 1 << 3,
    /// <summary><c>fashion_data</c> (field 8): <see cref="SocialSnapshot.Fashion"/>.</summary>
    Fashion = 1 << 4,
    /// <summary><c>scene_data</c> (field 10): <see cref="SocialSnapshot.Location"/>.</summary>
    Scene = 1 << 5,
    /// <summary><c>user_attr_data</c> (field 11): <see cref="SocialSnapshot.FightPoint"/>.</summary>
    Attr = 1 << 6,
    /// <summary><c>team_data</c> (field 12): <see cref="SocialSnapshot.Party"/>, <see cref="SocialIdentity.PartySize"/>.</summary>
    Team = 1 << 7,
    /// <summary><c>union_data</c> (field 13): <see cref="SocialIdentity.Guild"/>.</summary>
    Union = 1 << 8,
    /// <summary><c>personal_zone</c> (field 16): <see cref="SocialIdentity.TitleId"/> and the collection points.</summary>
    PersonalZone = 1 << 9,
    /// <summary><c>master_mode_dungeon_data</c> (field 22): <see cref="SocialIdentity.MasterScore"/>.</summary>
    MasterMode = 1 << 10,
}
