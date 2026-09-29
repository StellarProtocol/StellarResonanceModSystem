namespace Stellar.Application.Abstractions;

/// <summary>
/// Optional renderer capability: keep a hidden window's built UI tree alive but inactive, instead of destroying it
/// and rebuilding it on the next show. Rebuilding a large window (the combat meter's 44 rows) through IL2CPP cost a
/// 350-385 ms frame per show (owner report 2026-09-30). A renderer without it keeps the destroy-on-hide behaviour.
/// </summary>
internal interface IWindowParking
{
    /// <summary>Deactivate a mounted window (no per-frame cost, no input, no raycasts) without destroying it.</summary>
    void Park(object? token);

    /// <summary>Re-show a parked window at the top of its stacking tier, as a fresh mount would be.</summary>
    void Unpark(object? token);
}
