using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Concrete <see cref="IPlayerState"/>. Holds the last successful snapshot from
/// the outbound <see cref="IPlayerStateProbe"/>; Host calls <see cref="Refresh"/>
/// once per game tick.
/// </summary>
internal sealed class PlayerStateService : IPlayerState
{
    private PlayerStateSnapshot _snapshot;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;

    public string? Name => _isAvailable ? _snapshot.Name : null;
    public int Level => _isAvailable ? _snapshot.Level : 0;
    public int Profession => _isAvailable ? _snapshot.Profession : 0;
    public int Health => _isAvailable ? _snapshot.Health : 0;
    public int MaxHealth => _isAvailable ? _snapshot.MaxHealth : 0;
    public int Stamina => _isAvailable ? _snapshot.Stamina : 0;
    public int MaxStamina => _isAvailable ? _snapshot.MaxStamina : 0;
    public Position3D Position => _isAvailable ? _snapshot.Position : Position3D.Zero;

    /// <summary>
    /// Polls the probe and replaces the cached snapshot on success. On failure
    /// the previous snapshot stays put but <see cref="IsAvailable"/> drops to
    /// <c>false</c> so consumers stop trusting stale values across logout / scene
    /// teardown.
    /// </summary>
    internal void Refresh(IPlayerStateProbe probe)
    {
        if (probe.TrySample(out var snapshot))
        {
            _snapshot = snapshot;
            _isAvailable = true;
        }
        else
        {
            _isAvailable = false;
        }
    }
}
