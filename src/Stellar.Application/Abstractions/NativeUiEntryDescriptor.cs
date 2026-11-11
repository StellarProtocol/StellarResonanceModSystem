using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Where the framework puts its wrapper RectTransform for an allowlist element (see <see cref="INativeUiAdapter"/>).
/// </summary>
internal enum NativeUiWrapMode
{
    /// <summary>Wrapper becomes the element's PARENT (at the element's sibling index). For in-prefab nodes the
    /// game reaches through serialized binder refs only — verified safe live 2026-11-11.</summary>
    Outer,

    /// <summary>Wrapper becomes the element's first CHILD and holds all its children. For VIEW ROOTS (chat, boss
    /// bar) — the game detaches/re-parents those to <c>UILayerMain</c> on every show, orphaning an outer wrapper —
    /// and for nodes under a LayoutGroup (class gauge).</summary>
    Inner,
}

/// <summary>
/// Application's view of one allowlist entry. Infrastructure's NativeUiAllowlist projects its richer record onto
/// this so Application doesn't depend on Infrastructure types. DisplayNameKey is a framework localization key
/// (<c>gameui.entry.*</c>), resolved at render time by the consumers — not display text itself.
/// </summary>
internal sealed record NativeUiEntryDescriptor(string Id, string DisplayNameKey, string Path)
{
    public bool SafeToHide { get; init; } = true;

    /// <summary>Optional sub-path (relative to <see cref="Path"/>) of the descendant whose own screen-rect is
    /// used as the edit-mode outline / grab-box; null → adapter computes it. See
    /// <c>NativeUiAllowlistEntry.RectChild</c>.</summary>
    public string? RectChild { get; init; }

    /// <summary>Wrapper placement (default <see cref="NativeUiWrapMode.Outer"/>).</summary>
    public NativeUiWrapMode Wrap { get; init; } = NativeUiWrapMode.Outer;

    /// <summary>While hidden, also make the element non-interactive (a CanvasGroup on OUR wrapper) — the chat
    /// input must not take Enter focus while invisible.</summary>
    public bool BlockInputWhenHidden { get; init; }

    /// <summary>Edit-mode placeholder rect used before the element was ever seen, as SCREEN FRACTIONS (0..1,
    /// top-left / y-down). Width/height 0 → no built-in default (a small centred box).</summary>
    public WindowRect DefaultFraction { get; init; }

    /// <summary>Optional placeholder hint key replacing the generic "Hidden by the game now".</summary>
    public string? HintKey { get; init; }

    /// <summary>Optional placeholder sub line key (shown under the hint while not hidden by the user).</summary>
    public string? SubHintKey { get; init; }

    /// <summary>Optional names of SIBLING nodes the game shows instead of the element (outer wrap only); they share
    /// its wrapper, so offset + hide apply to them too. See <c>NativeUiAllowlistEntry.Twins</c>.</summary>
    public string[]? Twins { get; init; }
}
