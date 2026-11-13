using System;
using System.Collections.Concurrent;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Per-player cache of the latest social-data reply. No eviction (matches the inspector's
/// sticky-skill policy — a snapshot stays valid until replaced by a fresher reply). Thread-safe push
/// (decode runs on the wire thread); reads on the UI thread.
/// <para>Merged per SECTION, not last-reply-wins. The game asks for a subset of sections per request (the ID card
/// asks for all; nameplates/portraits ask for basic+avatar+personal_zone; some panels ask for basic only), and a
/// reply omits every section it wasn't asked for. A plain replace let a thin reply wipe a full one — ability score,
/// class, gear and outfit dropped to 0/empty within seconds, and the location was gone too (~60 thin vs ~17 full
/// replies in one session). So a section the new reply carries replaces the cached one; a section it doesn't carry
/// is kept from the cached snapshot (see <see cref="SocialSnapshot.Sections"/>).</para>
/// <para><see cref="SocialSnapshot.Location"/>, <see cref="SocialSnapshot.Party"/> and <see cref="SocialSnapshot.Presence"/>
/// are carried forward whenever the new reply's value is null (their own null already means "section absent").
/// Location and presence carry a <c>ReceivedAtMs</c> stamp telling the consumer how old they are; party has none.</para>
/// <para>A snapshot with <see cref="SocialSections.None"/> (built by hand, not decoded) keeps the old
/// replace-everything behaviour for the value sections.</para></summary>
public sealed class SocialDataCache : ISocialDataSink
{
    private readonly ConcurrentDictionary<long, SocialSnapshot> _byChar = new();
    private readonly Func<long> _serverNowMs;

    /// <param name="serverNowMs">Framework interpolated server clock (Unix ms, 0 = pre-sync) used to stamp
    /// <see cref="SocialLocation.ReceivedAtMs"/> and <see cref="SocialPresence.ReceivedAtMs"/>. Null → every stamp
    /// is 0 (tests / no clock).</param>
    public SocialDataCache(Func<long>? serverNowMs = null) => _serverNowMs = serverNowMs ?? (static () => 0L);

    /// <summary>Store the latest social snapshot for a player, keyed by charId, merged per section with the cached one.
    /// A fresh location / presence is stamped with the server clock.</summary>
    public void Push(SocialSnapshot snapshot)
    {
        if (snapshot.Location.HasValue || snapshot.Presence.HasValue)
        {
            long now = _serverNowMs();
            snapshot = snapshot with
            {
                Location = snapshot.Location is { } l ? l with { ReceivedAtMs = now } : null,
                Presence = snapshot.Presence is { } pr ? pr with { ReceivedAtMs = now } : null,
            };
        }

        // AddOrUpdate so the merge reads the value it replaces, even with two pushes racing.
        _byChar.AddOrUpdate(snapshot.CharId,
            static (_, incoming) => incoming,
            static (_, old, incoming) => Merge(old, incoming),
            snapshot);
    }

    // Per-section merge (pure; the class remarks give the why). Runs once per social reply — not a hot path.
    internal static SocialSnapshot Merge(SocialSnapshot old, SocialSnapshot incoming)
    {
        var merged = incoming with
        {
            Location = incoming.Location ?? old.Location,
            Party    = incoming.Party    ?? old.Party,
            Presence = incoming.Presence ?? old.Presence,
        };
        var have = incoming.Sections;
        if (have == SocialSections.None) return merged;          // hand-built: no section info → replace
        var keep = old.Sections & ~have;
        if (keep == SocialSections.None) return merged;

        static bool K(SocialSections keep, SocialSections s) => (keep & s) != 0;
        var oi = old.Identity; var ni = incoming.Identity;
        return merged with
        {
            Name         = K(keep, SocialSections.Basic)      ? old.Name         : incoming.Name,
            Level        = K(keep, SocialSections.Basic)      ? old.Level        : incoming.Level,
            ProfileUrl   = K(keep, SocialSections.Avatar)     ? old.ProfileUrl   : incoming.ProfileUrl,
            HalfBodyUrl  = K(keep, SocialSections.Avatar)     ? old.HalfBodyUrl  : incoming.HalfBodyUrl,
            ProfessionId = K(keep, SocialSections.Profession) ? old.ProfessionId : incoming.ProfessionId,
            Gear         = K(keep, SocialSections.Equip)      ? old.Gear         : incoming.Gear,
            Fashion      = K(keep, SocialSections.Fashion)    ? old.Fashion      : incoming.Fashion,
            FightPoint   = K(keep, SocialSections.Attr)       ? old.FightPoint   : incoming.FightPoint,
            Identity = new SocialIdentity(
                K(keep, SocialSections.Union)        ? oi.Guild             : ni.Guild,
                K(keep, SocialSections.Team)         ? oi.PartySize         : ni.PartySize,
                K(keep, SocialSections.MasterMode)   ? oi.MasterScore       : ni.MasterScore,
                K(keep, SocialSections.PersonalZone) ? oi.TitleId           : ni.TitleId,
                K(keep, SocialSections.PersonalZone) ? oi.FashionCollect    : ni.FashionCollect,
                K(keep, SocialSections.PersonalZone) ? oi.RideCollect       : ni.RideCollect,
                K(keep, SocialSections.PersonalZone) ? oi.WeaponSkinCollect : ni.WeaponSkinCollect),
            Sections = have | keep,
        };
    }

    /// <summary>Latest snapshot for the entity, or null if none received / not a player.</summary>
    public SocialSnapshot? GetSocialSnapshot(EntityId entity)
        => entity.IsPlayer && _byChar.TryGetValue(entity.Value >> 16, out var s) ? s : null;

    /// <summary>Latest snapshot for a player by charId, or null if none received yet.</summary>
    public SocialSnapshot? GetByCharId(long charId)
        => _byChar.TryGetValue(charId, out var s) ? s : null;

    /// <summary>Drop every cached per-player social snapshot on logout (account/character-scoped).
    /// Called by the Host OnLogout dispatcher so the next account can't read the previous session's
    /// social data.</summary>
    internal void ClearSession() => _byChar.Clear();
}
