using System;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Seam between the shared window builders and the game-only TMP bold-text implementation. The builders
/// (compiled into BOTH the game framework and the Mono UI-sandbox) render REAL-bold titles/headers through
/// this hook when a game-only implementation registered one (<c>TmpStyledText.Register()</c>, done by
/// <c>WindowRenderer</c>); when it is null — in the sandbox, or when the shipped bold face failed to
/// load — they fall back to the legacy crisp uGUI Text (Latin synthetic bold, complex scripts regular;
/// see <see cref="GlyphScript"/>). This file must stay TMP-free: it is symlinked into the sandbox.
/// </summary>
internal static class StyledTextFactory
{
    /// <summary>Game-only factory for a real-bold text element. Null → callers use the legacy Text path.</summary>
    public static Func<Transform, StyledTextSpec, IStyledTextHandle?>? CreateBold;
}

/// <summary>Creation inputs for a real-bold text element (initial values; live updates go through the handle).</summary>
internal readonly struct StyledTextSpec
{
    public readonly string Text;
    public readonly int FontSize;
    public readonly Color Color;
    public readonly bool Wrap;

    public StyledTextSpec(string text, int fontSize, Color color, bool wrap)
    {
        Text = text; FontSize = fontSize; Color = color; Wrap = wrap;
    }
}

/// <summary>Live handle to a built bold-text element (TMP in-game; the sandbox never sees one).</summary>
internal interface IStyledTextHandle
{
    /// <summary>The element's GameObject (layout attachment point); null after destruction.</summary>
    GameObject? Go { get; }

    /// <summary>Set the displayed string (re-picks the per-script face on a live language switch).</summary>
    void SetText(string s);

    /// <summary>Set the font size in px (already theme-scaled by the caller).</summary>
    void SetFontSize(int px);

    /// <summary>Set the text colour.</summary>
    void SetColor(Color c);
}
