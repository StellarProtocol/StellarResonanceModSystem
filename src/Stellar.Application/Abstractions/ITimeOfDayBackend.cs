namespace Stellar.Application.Abstractions;

/// <summary>
/// The game side of time of day (the game's own time-of-day bridge calls; never the server clock). Reads return
/// null when they cannot be answered. Writes never throw. Main thread only.
/// </summary>
internal interface ITimeOfDayBackend
{
    /// <summary>The pin/release calls resolved on this client.</summary>
    bool IsAvailable { get; }
    /// <summary>Available AND the world is in a stable scene (the environment system is valid).</summary>
    bool IsReady { get; }
    float? ReadHour();
    bool? ReadServerDriven();
    /// <summary>Stops server-driven time, then snaps the clock to <paramref name="hour"/> (no cross-fade).</summary>
    void Pin(float hour);
    /// <summary>Hands time of day back to the server clock.</summary>
    void ReleaseToServer();
    /// <summary>Installs the game re-assert hooks if not yet installed (lazy: first Pin). Idempotent.</summary>
    void EnsureHooks();
}
