using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Theme;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // Framework toast surface (INotifications). The service holds the active-toast queue;
    // this wiring renders it through the existing uGUI HUD toolkit (no bespoke renderer) as
    // a single HUD anchored top-centre, newest toast on top, one themed text row per toast.
    private NotificationService? _notificationService;

    // Per-tick snapshot of active toasts, NEWEST FIRST (the HUD draws row 0 at the top).
    // Refreshed once per tick by TickNotifications; the HUD's TextElement Funcs read it by index.
    private IReadOnlyList<ActiveToast> _toastSnapshot = Array.Empty<ActiveToast>();

    // Max simultaneously-rendered rows; older toasts beyond this are queued but not drawn.
    private const int ToastRowCount = 5;

    private void BuildNotificationServices(BepInExPluginLog log)
    {
        _notificationService = new NotificationService();

        var colors = _themeRenderer!.Colors;
        var slots = new List<HudElement>(ToastRowCount);
        for (var i = 0; i < ToastRowCount; i++)
        {
            var row = i;   // capture per-slot index
            slots.Add(new TextElement(
                Text: () => row < _toastSnapshot.Count ? _toastSnapshot[row].Message : string.Empty,
                Color: () => row < _toastSnapshot.Count ? ColorFor(colors, _toastSnapshot[row].Kind) : (ColorRgba?)null,
                Emphasis: false,
                Width: 0f,
                Align: TextAlign.Center,
                Shadow: true,   // chrome-less overlay over the live world — keep text legible
                FontSize: 0,
                ShadowDistance: 1));
        }

        var root = new ListElement(
            VisibleCount: () => Math.Min(_toastSnapshot.Count, ToastRowCount),
            Slots: slots);

        // Top-centre (ScreenCenterX ignores DefaultRect.X; Y = px from top). Show over the
        // world AND over game menus (a plugin guard message can fire from a menu context),
        // and from boot (not gated to in-world).
        var spec = new HudSpec(
            Id: "stellar.notifications",
            Anchor: HudAnchor.ScreenCenterX,
            Root: root,
            AutoHideBehindGameMenus: false,
            HideUntilInWorld: false,
            DefaultRect: new WindowRect(0f, 48f, 0f, 0f));

        _hudService!.Register(spec);
    }

    // Per-kind colour from the active theme. Re-read each HUD apply, so a theme switch is
    // picked up without re-registering the HUD.
    private static ColorRgba ColorFor(IThemeColors colors, NotificationKind kind) => kind switch
    {
        NotificationKind.Success => colors.Accent,
        NotificationKind.Warning => colors.Warning,
        NotificationKind.Error   => colors.Warning,
        _                        => colors.TextPrimary,
    };

    // Pull the live, non-expired toast set into the snapshot the HUD reads, newest first.
    // Driven from the host per-tick service path. No-op-cheap when no toasts are active.
    private void TickNotifications()
    {
        if (_notificationService is null) return;
        var active = _notificationService.Drain(_notificationService.Now);
        if (active.Count == 0)
        {
            if (_toastSnapshot.Count != 0) _toastSnapshot = Array.Empty<ActiveToast>();
            return;
        }

        // Newest first: the renderer draws slot 0 at the top.
        var newestFirst = new ActiveToast[active.Count];
        for (var i = 0; i < active.Count; i++) newestFirst[i] = active[active.Count - 1 - i];
        _toastSnapshot = newestFirst;
    }
}
