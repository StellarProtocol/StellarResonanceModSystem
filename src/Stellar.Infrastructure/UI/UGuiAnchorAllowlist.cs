using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Hand-curated map of <see cref="NativeUiAnchor"/> → live game hierarchy paths
/// (mirrors NativeUiAllowlist). InsertionParentPath is the container we parent
/// into; TemplateChildName is the NAME of an existing child of that parent to
/// clone for native styling (MenuButton only) — matched against the parent's
/// live children rather than GameObject.Find, which can't disambiguate the many
/// same-named rail buttons. Captured via "Recon paths…" on build 2.11
/// (2026-06-01) with the Main Menu open.
/// </summary>
internal sealed record UGuiAnchorEntry(string InsertionParentPath, string? TemplateChildName);

internal static class UGuiAnchorAllowlist
{
    private const string MenuWindow = "zuiroot/UILayerMain/main_funcs_list_window_pc(Clone)";

    public static readonly IReadOnlyDictionary<NativeUiAnchor, UGuiAnchorEntry> Entries =
        new Dictionary<NativeUiAnchor, UGuiAnchorEntry>
        {
            // Main Menu right side-rail. InsertionParentPath (the menu window) is
            // only the availability probe; the actual clone target is found by
            // locating a LIVE main_sys_esc_btn_tpl_pc(Clone) anywhere in the scene
            // and cloning next to it — the exact content path proved unreliable
            // (wrong/empty instance, pooled scroll list).
            [NativeUiAnchor.MainMenuRail] = new(MenuWindow, "main_sys_esc_btn_tpl_pc(Clone)"),

            // Always-on world-HUD top-right container (built-from-scratch elements).
            [NativeUiAnchor.HudTopRight] = new(
                "zuiroot/UILayerMain/main_main_pc(Clone)/anim/node_main/node_upper_right",
                null),
        };

    public static bool TryGet(NativeUiAnchor anchor, out UGuiAnchorEntry entry)
        => Entries.TryGetValue(anchor, out entry!);
}
