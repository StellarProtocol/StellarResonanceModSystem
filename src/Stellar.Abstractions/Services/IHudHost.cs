using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>HUD placement. <see cref="FreeOverlay"/> = the draggable, position-persisted Stellar HUD layer.</summary>
public enum HudAnchor
{
    /// <summary>The draggable, position-persisted Stellar HUD canvas layer.</summary>
    FreeOverlay = 0,
    /// <summary>Centers the HUD horizontally at runtime using Unity's RectTransform anchor system.
    /// Works at any screen resolution without hardcoded pixel math. The X component of
    /// <see cref="HudSpec.DefaultRect"/> is ignored; only Y (distance from top) is applied.</summary>
    ScreenCenterX = 1,
    /// <summary>Centers the HUD vertically at runtime. The Y component of
    /// <see cref="HudSpec.DefaultRect"/> is ignored; only X (distance from left) is applied.</summary>
    ScreenCenterY = 2,
    /// <summary>Centers the HUD both horizontally and vertically. Both X and Y components of
    /// <see cref="HudSpec.DefaultRect"/> are ignored.</summary>
    ScreenCenter = 3,
}

/// <summary>A plugin's HUD: stable id, anchor, and the root element to render. <paramref name="AutoHideBehindGameMenus"/>
/// (default true) hides the HUD while a full-screen game menu is open — gameplay HUDs (cooldowns, meter, stats) should
/// not draw over the inventory/menu. <paramref name="HideUntilInWorld"/> keeps it hidden until the player is logged in.
/// <paramref name="DefaultRect"/> is the spawn position used on first run / when no layout is saved for the current
/// resolution (clamped on-screen by the layout layer); null falls back to the renderer's initial placement (top-left).</summary>
public sealed record HudSpec(string Id, HudAnchor Anchor, HudElement Root,
    bool AutoHideBehindGameMenus = true, bool HideUntilInWorld = false, WindowRect? DefaultRect = null)
{
    /// <summary>When set, evaluated at mount/reset time to compute the spawn position, overriding
    /// <see cref="DefaultRect"/>. Use with <see cref="IFramework.ScreenHeight"/> to scale the
    /// initial position with screen resolution.</summary>
    public Func<WindowRect>? DynamicDefaultRect { get; init; }
}

/// <summary>Plugin-facing toolkit: describe a HUD as composed <see cref="HudElement"/>s; the framework
/// builds native uGUI and owns rendering, lifecycle, refresh, and animation.</summary>
public interface IHudHost
{
    /// <summary>Register a HUD. Built when its anchor is present; returns a handle to manage it.</summary>
    IHudHandle Register(HudSpec spec);
}

/// <summary>Handle to a registered HUD. Auto-removed on plugin/framework dispose.</summary>
public interface IHudHandle
{
    /// <summary>True while the HUD is visible AND currently mounted.</summary>
    bool IsShown { get; }
    /// <summary>Optional hint: apply this HUD's values now rather than waiting for the next poll.
    /// Not required — the framework polls regardless, so forgetting it never freezes the HUD.</summary>
    void MarkDirty();
    /// <summary>Show or hide the HUD.</summary>
    void SetVisible(bool visible);
    /// <summary>Remove the HUD now and stop rebuilding it.</summary>
    void Remove();
}
