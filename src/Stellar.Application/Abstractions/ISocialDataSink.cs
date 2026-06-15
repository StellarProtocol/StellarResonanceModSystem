using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Inbound push for decoded social-data replies (Infrastructure → Application). The charId
/// is mapped to the live <see cref="Stellar.Abstractions.Domain.EntityId"/> by the cache.</summary>
public interface ISocialDataSink
{
    /// <summary>Store/replace the latest social snapshot for a player.</summary>
    void Push(SocialSnapshot snapshot);
}
