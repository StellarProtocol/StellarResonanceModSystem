using System;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

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

    /// <summary>Game-only factory for the TMP rich-emoji text field (InputElement.RichEmoji): builds the
    /// TMP_InputField onto the given (already boxed) root. Null → UGuiTextInput builds the legacy InputField.</summary>
    public static Func<GameObject, RichInputSpec, IRichInputField?>? CreateRichInput;

    /// <summary>One-line diagnostics sink for the shared builders (wired with the factories).</summary>
    public static Action<string>? Log;

    /// <summary>diag: total glyphs in the framework's TMP dynamic atlases (game-only; wired only while [Hitch]
    /// attribution is on). Null → the apply-phase glyph counters are skipped.</summary>
    public static Func<int>? GlyphCount;
}

/// <summary>Creation inputs for a rich-emoji text field. <see cref="Target"/> is the root's bg Image (the
/// Selectable targetGraphic); the callbacks are UGuiTextInput's own onValueChanged / single-line onSubmit
/// handlers, so the strip/submit/grace logic stays in one place for both field kinds.</summary>
internal readonly struct RichInputSpec
{
    public readonly bool SingleLine;
    public readonly Graphic Target;
    public readonly Action<string> OnValueChanged;
    public readonly Action<string> OnSubmit;

    public RichInputSpec(bool singleLine, Graphic target, Action<string> onValueChanged, Action<string> onSubmit)
    {
        SingleLine = singleLine; Target = target; OnValueChanged = onValueChanged; OnSubmit = onSubmit;
    }
}

/// <summary>The few field operations UGuiTextInput needs, so the TMP field (game-only) sits behind the same
/// class as the legacy InputField without this sandbox-shared code touching TMP types.</summary>
internal interface IRichInputField
{
    /// <summary>Raw field focus (TMP_InputField.isFocused); UGuiTextInput adds the submit grace on top.</summary>
    bool IsFocused { get; }

    /// <summary>Current text, tags included.</summary>
    string Text { get; }

    /// <summary>Framework write (external Get() change or newline strip). Fires onValueChanged like the legacy
    /// text setter; never treated as a user edit. <paramref name="caretToEnd"/> moves the caret to the end.</summary>
    void SetText(string value, bool caretToEnd);

    /// <summary>Text/placeholder/caret colour + horizontal inner padding (the bg is themed by the owner).</summary>
    void ApplyStyle(Color textColor, float leftPad);

    /// <summary>Select + ActivateInputField; false while the GameObject is inactive in the hierarchy.</summary>
    bool TryActivateFocus();

    /// <summary>End editing (Esc / programmatic defocus).</summary>
    void Deactivate();

    /// <summary>Collapse the selection and put the caret at the END of the raw text (after a trailing tag) — the
    /// InputElement.FocusRequest caret placement, run once focus is confirmed.</summary>
    void CaretToEnd();

    /// <summary>Per-frame housekeeping (late sprite-asset resolve). Cheap no-op once settled.</summary>
    void Tick();
}

/// <summary>Creation inputs for a styled text element (initial values; live updates go through the
/// handle). Styles are per-element constants — only the string and colour are live-bound.</summary>
internal readonly struct StyledTextSpec
{
    public readonly string Text;
    public readonly int FontSize;
    public readonly Color Color;
    public readonly bool Wrap;

    /// <summary>Real-bold weight — a designed bold face per script, never synthetic bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Synthetic slant (script-safe in TMP's SDF rendering).</summary>
    public bool Italic { get; init; }

    /// <summary>Underline decoration (needs U+005F in the primary face — the shipped faces carry it).</summary>
    public bool Underline { get; init; }

    /// <summary>Strikethrough decoration.</summary>
    public bool Strikethrough { get; init; }

    /// <summary>Horizontal alignment (TextAlign.Left default).</summary>
    public TextAlign Align { get; init; }

    /// <summary>Parse TMP rich text and draw inline <c>&lt;sprite=N&gt;</c> tags from the GAME's chat sprite
    /// asset (TextElement.RichEmoji — see <c>TmpEmojiSprites</c>). Off = the styled path is unchanged.</summary>
    public bool RichEmoji { get; init; }

    public StyledTextSpec(string text, int fontSize, Color color, bool wrap)
    {
        Text = text; FontSize = fontSize; Color = color; Wrap = wrap;
        Bold = false; Italic = false; Underline = false; Strikethrough = false; Align = TextAlign.Left;
        RichEmoji = false;
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

    /// <summary>Force one mesh regeneration. The game's TMP build drops the underline segment on a
    /// text's FIRST post-layout generation (measured in-game 2026-08-18: the line appears only after any
    /// later regeneration; strikethrough is unaffected) — the binding calls this once shortly after the
    /// first paint so decorations are deterministic.</summary>
    void Refresh();

    /// <summary>True when the current string must be re-applied even though it did not change — a RichEmoji
    /// text whose sprite asset resolved (lazily, in World) AFTER its string was set, so its literal
    /// <c>&lt;sprite=N&gt;</c> tags can now draw as images. Always false for a non-RichEmoji handle.</summary>
    bool Stale { get; }
}
