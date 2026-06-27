namespace Stellar.Abstractions.Domain;

/// <summary>
/// Single source of truth for the framework's version string. Lives in
/// Abstractions so every layer (Host's BepInPlugin manifest, Infrastructure's
/// AboutPanel, and any plugin that wants to gate behaviour on a particular
/// framework release) can read the same constant without re-declaring it.
/// Bump this on each user-visible release; Host's <c>BootstrapPlugin.PluginVersion</c>
/// forwards to it so the BepInEx plugin manifest stays in lockstep.
/// </summary>
/// <remarks>
/// BepInEx parses <see cref="Value"/> with SemVer semantics — it rejects
/// trailing letters like <c>0.9.0a</c> ("Skipping type because its version
/// is invalid"). Use SemVer pre-release suffix syntax (<c>0.9.0-alpha</c>)
/// so the chainloader accepts the manifest.
/// </remarks>
public static class FrameworkVersion
{
    /// <summary>
    /// Current framework version. Plain SemVer (no pre-release suffix) keeps the
    /// BepInEx chainloader happy. 1.7.0 adds <b>per-plugin &amp; dynamic update-rate control</b>:
    /// each plugin's <c>IFramework.Update</c> ticks at its own rate (user-configurable in
    /// Settings → Performance), and a plugin may temporarily ramp its own rate via
    /// <c>IFramework.RequestUpdateRate</c> (returns an <c>IUpdateRateScope</c>), gated by a
    /// per-plugin user permission. The framework tick became a single variable-speed clock at
    /// <c>max(global, all active rates)</c>, with the Lua-bridge probe drains riding it so RPC
    /// latency falls with the rate. Also adds <c>SliderElement.Width</c>/<c>HandleSize</c> and
    /// honors <c>ToggleElement.Enabled</c>. 1.6.0 adds <c>IExchange</c> (player Trading-Center
    /// query + buy via the game's own trade flow, exposed as <c>IPluginServices.Market</c>)
    /// and the extensible <c>INotifications.Create()</c> toast builder (custom-icon support).
    /// 1.5.0 added team-voice mic status + dungeon
    /// ready-check on the meter row (<c>IPartyEvents</c>/<c>IPartyRoster</c>) plus
    /// the click-away popup / <c>PanelElement</c> / z-ordering UI primitives. 1.4.2
    /// was a hotfix for non-ASCII (e.g. Thai) text input truncated in uGUI fields
    /// (onValidateInput → onValueChanged); 1.4.1 scoped the rail-button template
    /// lookup (periodic freeze); 1.4.0 added <c>IWindowControl.SetVisiblePersist</c>
    /// plus the native-UI grab-box / cutscene-reposition fixes.
    /// </summary>
    public const string Value = "1.7.0";
}
