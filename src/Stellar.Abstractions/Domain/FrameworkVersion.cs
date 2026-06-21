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
    /// BepInEx chainloader happy. 1.4.0 adds <c>IWindowControl.SetVisiblePersist</c>
    /// (persist a window's visibility to the active layout slot so it survives a
    /// relaunch) to the plugin surface — minor bump = additive API — and fixes
    /// native-UI edit-mode grab-boxes collapsing to the corner during loading /
    /// cutscenes plus game-UI elements flinging off-screen after a cutscene.
    /// </summary>
    public const string Value = "1.4.0";
}
