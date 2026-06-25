using Stellar.Abstractions.Domain;

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
/// Fluent builder for a single toast. Accumulates message / kind / duration / icon, then
/// <see cref="Show"/> enqueues the toast. Mirrors <see cref="INoticeTipBuilder"/>'s shape for
/// surface consistency. Obtain one via <see cref="INotifications.Create"/>.
/// </summary>
public interface INotificationBuilder
{
    /// <summary>The text to display. The toast is suppressed at <see cref="Show"/> if blank.</summary>
    /// <param name="message">Toast body text.</param>
    INotificationBuilder WithMessage(string message);

    /// <summary>Severity / intent, used to colour the toast and pick the default kind glyph.</summary>
    /// <param name="kind">The notification kind.</param>
    INotificationBuilder WithKind(NotificationKind kind);

    /// <summary>How long the toast stays on screen (seconds). Defaults to ~3 seconds when unset.</summary>
    /// <param name="seconds">Lifetime in seconds; must be positive or the toast is suppressed at <see cref="Show"/>.</param>
    INotificationBuilder WithDuration(float seconds);

    /// <summary>
    /// Show a custom icon in the toast's 16px icon slot instead of the baked kind glyph.
    /// </summary>
    /// <param name="texture">A boxed <c>UnityEngine.Texture</c> handle (e.g. from
    /// <see cref="IGameAssets.LoadItemIcon(int, out UvRect)"/>). Pass <c>null</c> to keep the baked kind glyph.</param>
    /// <param name="uv">The atlas UV sub-rect for the texture (ignored when <paramref name="texture"/> is null).</param>
    INotificationBuilder WithIcon(object? texture, UvRect uv);

    /// <summary>Enqueue the toast with the accumulated settings. Fire-and-forget; no handle is returned.</summary>
    void Show();
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

    /// <summary>Create a fluent builder for a toast with optional custom icon. Call
    /// <see cref="INotificationBuilder.Show"/> to enqueue it.</summary>
    INotificationBuilder Create();
}
