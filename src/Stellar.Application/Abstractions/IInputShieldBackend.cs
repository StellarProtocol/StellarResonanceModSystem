namespace Stellar.Application.Abstractions;

/// <summary>Game side of the input shield: the game's own input-ignore mask under ONE source no game script uses, owned by
/// <see cref="Services.InputShieldService"/> alone. It holds the UNION of two layers — the free camera's mask and the scene
/// freeze's pause block (movement and combat only) — and moves between unions by clearing only the bits no longer wanted
/// and setting the wanted ones, so dropping one layer never clears the other's bits (qa M-4, 2026-10-03). Main thread.</summary>
internal interface IInputShieldBackend
{
    /// <summary>Makes the mask exactly the union of the requested layers (none = everything this source set is cleared);
    /// false when the game call failed.</summary>
    bool Apply(bool camera, bool pause);

    /// <summary>The game may have rebuilt its ignore table (a zone load): the next <see cref="Apply"/> sets every wanted bit
    /// again instead of only the difference from the last one.</summary>
    void Forget();
}
