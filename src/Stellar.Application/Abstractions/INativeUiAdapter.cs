using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface implemented in Infrastructure (PandaHudAdapter) and
/// consumed by Application's NativeUiService. Resolves an allowlist path to
/// a handle representing a Panda.Hud Canvas RectTransform, and applies
/// position / visibility mutations against it.
/// </summary>
internal interface INativeUiAdapter
{
    /// <summary>
    /// Walk the Canvas hierarchy and resolve <paramref name="allowlistPath"/>
    /// to a handle, caching the underlying GameObject + original pose. Returns
    /// false until the game has constructed the target (in-world).
    /// <paramref name="rectChildPath"/> (nullable) is a sub-path under the resolved node whose own screen-rect
    /// becomes the handle's <see cref="NativeUiHandle.OriginalRect"/> (the edit outline / grab box); null →
    /// the adapter computes the rect from the node / visible-drawable union.
    /// </summary>
    bool TryResolve(string allowlistPath, string? rectChildPath, out NativeUiHandle handle);

    /// <summary>Toggle GameObject.SetActive on the resolved element.</summary>
    void SetVisible(NativeUiHandle handle, bool visible);

    /// <summary>Apply a top-left-anchored screen-space rect to the resolved RectTransform.</summary>
    void SetRect(NativeUiHandle handle, WindowRect rect);

    /// <summary>
    /// Restore the captured original RectTransform pose (anchorMin/Max, pivot,
    /// anchoredPosition, sizeDelta) AND active-self flag for the resolved
    /// element. Called from <c>NativeUiService.OnFrameworkDispose</c> on host
    /// shutdown so unloading the framework leaves the game's HUD anchoring
    /// configuration in the exact state we found it. <see cref="SetRect"/>
    /// re-anchors to (0,1) which by itself is NOT a full restore.
    /// </summary>
    void RestoreOriginal(NativeUiHandle handle);

    /// <summary>True while the resolved element behind <paramref name="handle"/> still exists. Goes false when
    /// the game destroys it (scene change) — the service uses this to re-resolve + re-apply the saved layout to
    /// the rebuilt element (bug #4: layout lost on scene change).</summary>
    bool IsAlive(NativeUiHandle handle);

    /// <summary>The element's current curated screen rect, recomputed live (tracks animations / layout changes
    /// such as the party panel resizing between 5- and 20-person). Used for the edit outline + hit-test.</summary>
    WindowRect GetCurrentRect(NativeUiHandle handle);

    /// <summary>Diagnostic-only (self-gates on <c>StellarDiagnostics.IsEnabled</c>): dump, for every resolved
    /// element, its own node screen-rect, the computed content rect the editor outline uses, and each drawable
    /// descendant's screen-rect + visibility — so an oversized outline can be traced to the child inflating it
    /// (bug #5). No-op when diagnostics are off.</summary>
    void DumpDiagnostics(Action<string> log);
}

/// <summary>
/// Opaque handle returned by <see cref="INativeUiAdapter.TryResolve"/>. Only
/// the adapter knows the meaning of <see cref="GameObjectRef"/>; Application
/// code carries the value through unchanged.
/// </summary>
internal readonly struct NativeUiHandle
{
    public string     AllowlistPath { get; init; }
    public IntPtr     GameObjectRef { get; init; }
    public WindowRect OriginalRect  { get; init; }
}
