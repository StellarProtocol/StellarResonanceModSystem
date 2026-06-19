namespace Stellar.Abstractions.Services;

/// <summary>Severity / intent of a transient toast, used by the framework to pick a colour.</summary>
public enum NotificationKind
{
    /// <summary>Neutral informational message (uses the primary text colour).</summary>
    Info,
    /// <summary>A positive / success outcome (uses the accent colour).</summary>
    Success,
    /// <summary>A non-fatal warning (uses the warning colour).</summary>
    Warning,
    /// <summary>An error / failure (uses an error colour).</summary>
    Error,
}

/// <summary>
/// Framework toast surface — show short, transient on-screen messages from any plugin.
/// Messages auto-disappear after their lifetime; this is fire-and-forget and read-only
/// (no dismissal handle, no input). Use for plugin-side feedback the game does not show
/// itself (guard / edge messages).
/// </summary>
public interface INotifications
{
    /// <summary>Show a transient on-screen toast.</summary>
    /// <param name="message">The text to display.</param>
    /// <param name="kind">Severity / intent, used to colour the toast. Defaults to <see cref="NotificationKind.Info"/>.</param>
    /// <param name="seconds">How long the toast stays on screen; defaults to ~3 seconds when null.</param>
    void Notify(string message, NotificationKind kind = NotificationKind.Info, float? seconds = null);
}
