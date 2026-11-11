using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface implemented in Infrastructure (PandaHudAdapter) and consumed by Application's
/// NativeUiService. Resolves an allowlist path to a handle for a Panda.Hud RectTransform and owns the
/// framework's WRAPPER around it: the user's offset + hide live on a RectTransform WE insert (outer = the
/// element's new parent, inner = the element's new first child holding its children), so the game keeps
/// writing its own node and the two compose — we never translate or SetActive a game node.
/// See Knowledge Base <c>Native-UI-Layout-Positioning.md</c> ("Wrapper spike 2026-11-11").
/// </summary>
internal interface INativeUiAdapter
{
    /// <summary>
    /// Resolve <paramref name="entry"/>'s full allowlist path to a handle (walks the path segment by segment, so
    /// an inactive element resolves too). Never name-searches (photo mode has a second FighterBtnsView copy).
    /// Allocation-free on a miss; returns false until the game has built the element.
    /// </summary>
    bool TryResolve(NativeUiEntryDescriptor entry, out NativeUiHandle handle);

    /// <summary>True while the resolved element behind <paramref name="handle"/> still exists. Goes false when the
    /// game destroys it (scene change rebuilds the battle HUD + chat) — the service then re-resolves + re-wraps.</summary>
    bool IsAlive(NativeUiHandle handle);

    /// <summary>Cheap liveness of OUR wrapper: outer → the element's parent is still our wrapper; inner → our
    /// wrapper is still the element's child. False also when the element was never wrapped. Polled at ≤10 Hz for
    /// customized elements only.</summary>
    bool IsWrapIntact(NativeUiHandle handle);

    /// <summary>Wrap the element if needed, then set the wrapper offset (<paramref name="offsetX"/>/<paramref name="offsetY"/>,
    /// the wrapper parent's local units, y-up) and the hide (<c>localScale</c> 0 / 1). Needs no measurement, so
    /// it works while the element is inactive / loading — no snap-back frame after a rebuild.</summary>
    void Apply(NativeUiHandle handle, float offsetX, float offsetY, bool visible);

    /// <summary>Remove our wrapper, restoring the element (outer) or its children (inner) to the original parent
    /// at the original sibling index. Idempotent; never destroys a game node.</summary>
    void Unwrap(NativeUiHandle handle);

    /// <summary>Measure the element's curated screen rect (top-left / y-down screen px) AS IF SHOWN by us (a
    /// user hide is lifted for the measurement within the same frame), whether the GAME is showing it, and the
    /// screen px per wrapper-local unit. Walks the content union — call at a low rate or while editing.</summary>
    NativeUiMeasure Measure(NativeUiHandle handle);

    /// <summary>Diagnostic-only (self-gates on <c>StellarDiagnostics.IsEnabled</c>): dump, for every resolved
    /// element, its node rect, content rect and drawable descendants (bug #5 oversized outlines).</summary>
    void DumpDiagnostics(Action<string> log);
}

/// <summary>One measurement of a resolved element (see <see cref="INativeUiAdapter.Measure"/>).</summary>
/// <param name="Rect">Live curated screen rect (top-left / y-down px), at the current wrapper offset.</param>
/// <param name="Showing">True when the game currently draws it (active, not collapsed, CanvasGroup alpha &gt; 0).</param>
/// <param name="PxPerUnit">Screen px per wrapper-parent local unit; 0 = unknown.</param>
/// <param name="SeenAllowed">False when the entry's <see cref="NativeUiSeenGate"/> is closed (element in a transient
/// expanded state): the live outline still uses <paramref name="Rect"/>, but it must not become the last-seen rect.</param>
internal readonly record struct NativeUiMeasure(WindowRect Rect, bool Showing, float PxPerUnit, bool SeenAllowed = true);

/// <summary>
/// Opaque handle returned by <see cref="INativeUiAdapter.TryResolve"/>. Only the adapter knows the meaning of
/// <see cref="GameObjectRef"/>; Application code carries the value through unchanged.
/// </summary>
internal readonly struct NativeUiHandle
{
    public string AllowlistPath { get; init; }
    public IntPtr GameObjectRef { get; init; }
}
