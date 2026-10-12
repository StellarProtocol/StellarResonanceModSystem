using System;
using System.Collections.Concurrent;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Per-player cache of the latest social-data reply. No eviction (matches the inspector's
/// sticky-skill policy — a snapshot stays valid until replaced by a fresher reply). Thread-safe push
/// (decode runs on the wire thread); reads on the UI thread.
/// <para>Last-reply-wins EXCEPT for <see cref="SocialSnapshot.Location"/>: only full-mask (ID-card) replies
/// carry <c>scene_data</c>, and the game re-fetches the same player with thin nameplate/avatar masks far more
/// often (~60 thin vs ~17 full in one session) — a plain replace wiped a known location within seconds. A
/// location-less reply therefore inherits the previous snapshot's location (its own
/// <see cref="SocialLocation.ReceivedAtMs"/> stamp tells the consumer how old it is).</para></summary>
public sealed class SocialDataCache : ISocialDataSink
{
    private readonly ConcurrentDictionary<long, SocialSnapshot> _byChar = new();
    private readonly Func<long> _serverNowMs;

    /// <param name="serverNowMs">Framework interpolated server clock (Unix ms, 0 = pre-sync) used to stamp
    /// <see cref="SocialLocation.ReceivedAtMs"/>. Null → every stamp is 0 (tests / no clock).</param>
    public SocialDataCache(Func<long>? serverNowMs = null) => _serverNowMs = serverNowMs ?? (static () => 0L);

    /// <summary>Store/replace the latest social snapshot for a player, keyed by charId. A fresh location is
    /// stamped with the server clock; a missing one is carried forward from the cached snapshot.</summary>
    public void Push(SocialSnapshot snapshot)
    {
        if (snapshot.Location is { } fresh)
            snapshot = snapshot with { Location = fresh with { ReceivedAtMs = _serverNowMs() } };

        // AddOrUpdate so the carry-forward reads the value it replaces, even with two pushes racing.
        _byChar.AddOrUpdate(snapshot.CharId,
            static (_, incoming) => incoming,
            static (_, old, incoming) => incoming.Location is null && old.Location is not null
                ? incoming with { Location = old.Location }
                : incoming,
            snapshot);
    }

    /// <summary>Latest snapshot for the entity, or null if none received / not a player.</summary>
    public SocialSnapshot? GetSocialSnapshot(EntityId entity)
        => entity.IsPlayer && _byChar.TryGetValue(entity.Value >> 16, out var s) ? s : null;

    /// <summary>Drop every cached per-player social snapshot on logout (account/character-scoped).
    /// Called by the Host OnLogout dispatcher so the next account can't read the previous session's
    /// social data.</summary>
    internal void ClearSession() => _byChar.Clear();
}
