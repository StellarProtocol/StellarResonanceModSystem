using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>A plugin's free-camera facades, minted per enable (fresh owner keys) and released together on unload —
/// camera first (its release already resets every posed person), then posing (this plugin's people, as a backstop),
/// freeze and shield, then handlers (the spec § 7 release order).</summary>
internal sealed record FreeCameraScope(
    PluginCameraOverride? Camera, PluginInputShield? Shield, PluginSceneFreeze? Freeze, PluginEmotes? Emotes, PluginCombatState? Combat,
    PluginPosing? Posing = null)
{
    public static FreeCameraScope Mint(IPluginServices shared) => new(
        shared.CameraOverride is CameraOverrideService c ? new PluginCameraOverride(c, new object()) : null,
        shared.InputShield is InputShieldService s ? new PluginInputShield(s, new object()) : null,
        shared.SceneFreeze is SceneFreezeService f ? new PluginSceneFreeze(f, new object()) : null,
        shared.Emotes is { } e ? new PluginEmotes(e) : null,
        shared.CombatState is { } cs ? new PluginCombatState(cs) : null,
        shared.Posing is PosingService p ? new PluginPosing(p, new object()) : null);

    public void ReleaseAll()
    {
        Camera?.ReleaseAll();
        Posing?.ReleaseAll();
        Freeze?.ReleaseAll();
        Shield?.ReleaseAll();
        Emotes?.ReleaseAll();
        Combat?.ReleaseAll();
    }
}
