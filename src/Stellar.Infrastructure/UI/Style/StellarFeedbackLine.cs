using System;
using System.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.Style;

/// <summary>
/// The footer feedback line (SDL R13: every action that changes something off-screen confirms itself where the player
/// looks): after <see cref="Show"/> it reads "✓ message" in the theme's Success colour for <see cref="ShowSeconds"/>,
/// otherwise the muted idle hint (a one-line tip). One line at a fixed height (it never moves the window, R14),
/// ellipsized. "✓" is plain text, which the game font draws (bold ✓ is untrusted, SDL §6).
/// </summary>
internal sealed class StellarFeedbackLine
{
    public const float ShowSeconds = 3f;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly StellarStyle _s;
    private readonly Func<string> _idleHint;
    private readonly Func<float> _now;
    private string _message = "";
    private float _until = float.NegativeInfinity;

    /// <summary><paramref name="now"/> = a seconds clock (default: a process-wide monotonic stopwatch); only deltas are
    /// used, so any epoch works — but don't mix clocks for one line (Framework-Interop-Foundation.md, TimeNow).</summary>
    public StellarFeedbackLine(StellarStyle style, Func<string> idleHint, Func<float>? now = null)
    {
        _s = style;
        _idleHint = idleHint;
        _now = now ?? (() => (float)Clock.Elapsed.TotalSeconds);
    }

    /// <summary>Shows "✓ <paramref name="message"/>" for <see cref="ShowSeconds"/> (a new message restarts the timer).</summary>
    public void Show(string message)
    {
        _message = message ?? "";
        _until = _now() + ShowSeconds;
    }

    public bool IsShowing => _message.Length > 0 && _now() < _until;

    /// <summary>The line element (place it at the bottom of the window Column). Text and colour are live.</summary>
    public HudElement Element() => StellarStyle.FixedLine(new TextElement(
        () => IsShowing ? "✓ " + _message : _idleHint(),
        Color: () => IsShowing ? _s.Readable(_s.Success(), _s.Background()) : _s.Muted(),
        NoWrap: true) { Ellipsize = true });
}
