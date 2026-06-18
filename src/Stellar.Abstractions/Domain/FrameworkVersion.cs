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
    /// BepInEx chainloader happy. 1.1.1 publishes the plugin SDK to NuGet.org
    /// (Stellar.Abstractions / PluginContracts / Plugin.InteropRefs) so plugins
    /// build without the framework source or a game install; framework runtime
    /// unchanged from 1.1.0.
    /// </summary>
    public const string Value = "1.1.1";
}
