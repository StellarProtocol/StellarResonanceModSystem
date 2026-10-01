namespace Stellar.Application.Abstractions;

/// <summary>Game side of the input shield: the game's own input-ignore mask under a source no game script uses. Main thread.</summary>
internal interface IInputShieldBackend
{
    /// <summary>Raises (true) or drops (false) the mask; false when the game call failed.</summary>
    bool SetShield(bool on);
}
