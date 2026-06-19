using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // Framework toast surface (INotifications). The service holds the active-toast queue; the
    // animated ToastRenderer owns its own screen-space-overlay canvas (above the HUD), reading the
    // live set each tick and animating spawn/exit/reflow. No longer a HudSpec — the HUD toolkit's
    // ~10 Hz change-gated value pull can't drive smooth motion.
    private NotificationService? _notificationService;
    private ToastRenderer? _toastRenderer;

    private void BuildNotificationServices(BepInExPluginLog log)
    {
        _notificationService = new NotificationService();
        _toastRenderer = new ToastRenderer(log, _themeRenderer!.Colors, _notificationService);
        // Theme switch: rebake text colours + drop the canvas (cards rebuild next tick from the live set).
        _namedTheme!.ActiveChanged += _toastRenderer.InvalidateTheme;
    }

    // Drive the toast renderer from the per-tick overlay path. dt is the framework tick delta
    // (≈1/UpdateRateHz), NOT Time.deltaTime — so enter/exit/reflow converge at the right speed at
    // any tick rate. Cheap when nothing is active and nothing is animating.
    private void TickNotifications(float dt) => _toastRenderer?.Tick(dt);
}
