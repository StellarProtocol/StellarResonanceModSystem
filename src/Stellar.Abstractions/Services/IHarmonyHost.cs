namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-plugin owner of <see cref="HarmonyLib.Harmony"/> instances. Plugins still author their own patch
/// classes, but obtain the <see cref="HarmonyLib.Harmony"/> from here so the framework guarantees id
/// uniqueness (namespaced to the plugin) and unpatches every instance automatically when the plugin is
/// disposed — a plugin can no longer leak an un-unpatched instance across a soft enable/disable cycle.
/// Pairs with <see cref="StellarInterop.FindMethod"/> for resolving patch targets.
/// </summary>
public interface IHarmonyHost
{
    /// <summary>Creates a <see cref="HarmonyLib.Harmony"/> instance owned by this plugin. Its id is the plugin
    /// id, or <c>"&lt;pluginId&gt;.&lt;suffix&gt;"</c> when <paramref name="suffix"/> is non-empty (use a suffix to
    /// hold several independently-unpatchable instances). All instances created here are unpatched on plugin
    /// dispose.</summary>
    /// <param name="suffix">Optional id suffix for a distinct patch group; empty uses the bare plugin id.</param>
    HarmonyLib.Harmony Create(string suffix = "");
}
