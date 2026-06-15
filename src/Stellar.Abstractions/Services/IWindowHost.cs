using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>A registered interactive window: its spec + the root element to render. <paramref name="TitleLeading"/>
/// (when set) replaces the title-bar text (e.g. a logo + wordmark row); <paramref name="TitleTrailing"/> is built
/// right-aligned before the ✕ (e.g. mode/rotate toggles). Both null ⇒ the chrome draws the plain Title text.
/// <paramref name="OnClose"/> (when set) is invoked by the ✕ instead of just hiding the GameObject — wire it to
/// the window's <see cref="IWindowControl.SetVisible"/>(false) so <see cref="IWindowControl.IsShown"/> stays in
/// sync (otherwise a rail/hotkey toggle needs two presses to reopen after a ✕ close).</summary>
public sealed record WindowRegistration(WindowSpec Spec, HudElement Root,
    HudElement? TitleLeading = null, HudElement? TitleTrailing = null, Action? OnClose = null);

/// <summary>Plugin/framework-facing toolkit: describe an interactive window as composed elements; the
/// framework builds native uGUI and owns chrome, rendering, refresh, lifecycle, input gating, persistence.</summary>
public interface IWindowHost
{
    /// <summary>Register a window. Built when its canvas is available; returns a handle to manage it.</summary>
    IWindowControl Register(WindowRegistration registration);

    /// <summary>
    /// Registers a window AND declares a hotkey that toggles its visibility — convenience over the
    /// separate <see cref="Register(WindowRegistration)"/> + <see cref="IHotkeys.DeclareAction"/> pattern
    /// (dup-map Pattern A). The returned <see cref="IWindowControl"/> manages the window; the hotkey is
    /// owned by <paramref name="hotkeys"/> and lives for the duration of that service.
    /// </summary>
    IWindowControl Register(WindowRegistration registration, HotkeyAction toggleAction, IHotkeys hotkeys);

    /// <summary>Look up a registered window by id (host-side composition / sibling addressing).</summary>
    IWindowControl? Find(string id);
}

/// <summary>Handle to a registered interactive uGUI window. Auto-removed on plugin/framework dispose.</summary>
public interface IWindowControl
{
    /// <summary>True while the window is visible AND currently mounted in the scene.</summary>
    bool IsShown { get; }
    /// <summary>Hints to the framework to re-poll and apply this window's element values immediately rather than waiting for the next scheduled refresh.</summary>
    void MarkDirty();
    /// <summary>Show or hide the window; persists the user's visibility preference.</summary>
    void SetVisible(bool visible);
    /// <summary>Permanently remove the window from the scene and the window registry.</summary>
    void Remove();

    /// <summary>Current on-screen rect (position + size). <c>default</c> until the window is mounted.</summary>
    WindowRect Rect { get; }

    /// <summary>Move/resize the window (size honoured only for <see cref="WindowSpec.Resizable"/> windows). The
    /// new rect is persisted by the framework. Used by plugins that remember their own per-mode geometry.</summary>
    void SetRect(WindowRect rect);
}
