using System.Collections.Concurrent;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Per-player cache of the latest social-data reply. No eviction (matches the inspector's
/// sticky-skill policy — a snapshot stays valid until replaced by a fresher reply). Thread-safe push
/// (decode runs on the wire thread); reads on the UI thread.</summary>
public sealed class SocialDataCache : ISocialDataSink
{
    private readonly ConcurrentDictionary<long, SocialSnapshot> _byChar = new();

    /// <summary>Store/replace the latest social snapshot for a player, keyed by charId.</summary>
    public void Push(SocialSnapshot snapshot) => _byChar[snapshot.CharId] = snapshot;

    /// <summary>Latest snapshot for the entity, or null if none received / not a player.</summary>
    public SocialSnapshot? GetSocialSnapshot(EntityId entity)
        => entity.IsPlayer && _byChar.TryGetValue(entity.Value >> 16, out var s) ? s : null;
}
