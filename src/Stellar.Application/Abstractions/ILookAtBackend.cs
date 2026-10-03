namespace Stellar.Application.Abstractions;

/// <summary>Game side of the look-at-camera recipe (snapshot → apply → restore the snapshot). Main thread.</summary>
internal interface ILookAtBackend
{
    /// <summary>Snapshots the local player's look-at state and turns the head toward the camera.
    /// Idempotent: returns true without overwriting the held snapshot if one is already held.</summary>
    bool TryApply();
    /// <summary>Releases with the game's recipe and writes back what the snapshot calls for. No-op if the model changed.</summary>
    void Restore();
}
