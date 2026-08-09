using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Concrete <see cref="IPlayerState"/>. Holds the last successful snapshot from
/// the outbound <see cref="IPlayerStateProbe"/>; Host calls <see cref="Refresh"/>
/// once per game tick.
///
/// <para><b>Identity is decoupled from liveness.</b> Vitals and position are
/// only meaningful while the live world entity is readable, so they stay gated
/// on <see cref="IsAvailable"/>. Name / level / profession are NOT: they fall
/// back to the probe's char-record identity, which survives an entity attribute
/// blackout (relaunching while mounted — see
/// <c>docs/recon/playerstate-probe-mounted-blackout.md</c>). Before this split a
/// single failed attribute read blanked the name AND the class crest AND the hp
/// together, which is what surfaced as the CombatMeter's own row rendering the
/// literal <c>"Self"</c> with no crest.</para>
/// </summary>
internal sealed class PlayerStateService : IPlayerState
{
    private readonly IClientState _clientState;
    private PlayerStateSnapshot _snapshot;
    private bool _isAvailable;

    public PlayerStateService(IClientState clientState) => _clientState = clientState;

    // Sticky identity, sourced from the char record rather than the world
    // entity. Held across failed samples on purpose; only ever replaced by a
    // better (non-empty / positive) value, or dropped wholesale when the record
    // reports a DIFFERENT character (see RefreshIdentity).
    private long _identityCharId;
    private string? _identityName;
    private int _identityLevel;
    private int _identityProfession;

    public bool IsAvailable => _isAvailable;

    // Identity: prefer the live entity while it is readable (it tracks an
    // in-session profession switch immediately), else the sticky char record.
    public string? Name
    {
        get
        {
            var live = _isAvailable ? _snapshot.Name : null;
            return string.IsNullOrEmpty(live) ? _identityName : live;
        }
    }

    public int Level
    {
        get
        {
            var live = _isAvailable ? _snapshot.Level : 0;
            return live > 0 ? live : _identityLevel;
        }
    }

    public int Profession
    {
        get
        {
            var live = _isAvailable ? _snapshot.Profession : 0;
            return live > 0 ? live : _identityProfession;
        }
    }

    // Vitals + position stay strictly gated: a stale hp/position is worse than
    // a zero, and nothing can recover them from the char record.
    public int Health => _isAvailable ? _snapshot.Health : 0;
    public int MaxHealth => _isAvailable ? _snapshot.MaxHealth : 0;
    public int Stamina => _isAvailable ? _snapshot.Stamina : 0;
    public int MaxStamina => _isAvailable ? _snapshot.MaxStamina : 0;
    public Position3D Position => _isAvailable ? _snapshot.Position : Position3D.Zero;

    /// <summary>
    /// Polls the probe and replaces the cached snapshot on success. On failure
    /// the previous snapshot stays put but <see cref="IsAvailable"/> drops to
    /// <c>false</c> so consumers stop trusting stale vitals across logout /
    /// scene teardown. Identity is refreshed independently and survives that
    /// drop.
    /// </summary>
    [WorldGated]
    internal void Refresh(IPlayerStateProbe probe)
    {
        RefreshIdentity(probe);

        if (!_clientState.IsWorldActive) return;
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

    /// <summary>
    /// Drop all account/character-scoped session state on logout. <see cref="Refresh"/> is
    /// <c>[WorldGated]</c>, so it will NOT self-clear once the world goes inactive — without this the
    /// previous account's sticky name / level / class would persist into the next login (it survives
    /// an attribute blackout by design). Called by the Host OnLogout dispatcher (mirrors the dungeon
    /// reset). Runs on the Unity main thread.
    /// </summary>
    internal void ClearSession()
    {
        _snapshot = default;
        _isAvailable = false;
        _identityCharId = 0;
        _identityName = null;
        _identityLevel = 0;
        _identityProfession = 0;
    }

    // Merges a char-record identity read into the sticky fields. Never
    // downgrades a known value to empty/zero — the probe returns false (not an
    // empty struct) while the record is unreadable, so an absent field here
    // means "this read didn't carry it", not "it was cleared".
    private void RefreshIdentity(IPlayerStateProbe probe)
    {
        if (!probe.TryReadIdentity(out var identity))
        {
            return;
        }

        // Character switch: drop everything first so a stale name can never be
        // attributed to the new character.
        if (identity.CharId != 0 && _identityCharId != 0 && identity.CharId != _identityCharId)
        {
            _identityName = null;
            _identityLevel = 0;
            _identityProfession = 0;
        }

        if (identity.CharId != 0)
        {
            _identityCharId = identity.CharId;
        }
        if (!string.IsNullOrEmpty(identity.Name))
        {
            _identityName = identity.Name;
        }
        if (identity.Level > 0)
        {
            _identityLevel = identity.Level;
        }
        if (identity.Profession > 0)
        {
            _identityProfession = identity.Profession;
        }
    }
}
