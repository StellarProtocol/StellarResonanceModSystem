using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Photo lights: lamps placed in the world, the level at which characters take lamp light, and a key light and rim per
/// person. Everything is local and visual only, and belongs to the scene: the framework removes every lamp and puts every
/// game value back — exactly as it was — on the reasons the scene freeze ends on (a zone change or scene leave, a
/// cutscene, the game's own camera mode, a disconnect, the framework unloading), raising <see cref="Released"/>; a
/// plugin's lights also go when it unloads, and on its own <see cref="ResetAll"/>. Leaving a free camera keeps them.
/// <para><b>Lamps</b> join the game's own clustered lights: real pools of light on the ground and scene. On characters a
/// lamp is an even colour wash scaled by distance, range and colour — not a directional shade — and only while
/// <see cref="PeopleLevel"/> is above 0. For one-sided shading use a person's <see cref="KeyLight"/>.</para>
/// <para><b><see cref="PeopleLevel"/> is shared by the whole scene:</b> while it is raised every character near ANY lamp —
/// the town's own street lamps included — is tinted. Each plugin's level counts only while that SAME plugin has at least
/// one lamp on: the framework raises the game's value to the highest level among plugins that have a lamp on (another
/// plugin's level never lights people through your lamps), and puts the game's own value back the moment no plugin with a
/// level above 0 has a lamp on — unless the game itself has written that value since (a cutscene, its own weather), in
/// which case the game's value is left as the game set it.</para>
/// <para>People are addressed by entity id, as <see cref="IPosing"/> does: a person posed as a copy or an NPC stand-in is
/// lit on that visible model. A person who leaves loses their key light and rim. Main thread only.</para>
/// </summary>
public interface ILights
{
    /// <summary>True while the player is in the world and the scene has settled (the same window as
    /// <see cref="IPosing.IsAvailable"/>). Outside it every call is refused.</summary>
    bool IsAvailable { get; }

    /// <summary>Adds a lamp. Returns <see cref="LampId.None"/> when lights are not available, when this plugin already has
    /// <see cref="LightLimits.MaxLampsPerPlugin"/> lamps, or when the game refused it (logged).</summary>
    /// <param name="settings">Where the lamp is and how it shines.</param>
    LampId AddLamp(LampSettings settings);

    /// <summary>Changes a lamp of this plugin (move, recolour, on/off). False when the lamp is gone (removed, or the scene
    /// ended) or lights are not available.</summary>
    /// <param name="lamp">The lamp.</param>
    /// <param name="settings">Its new settings.</param>
    bool UpdateLamp(LampId lamp, LampSettings settings);

    /// <summary>Removes a lamp of this plugin. Nothing happens for a lamp that is already gone.</summary>
    /// <param name="lamp">The lamp.</param>
    void RemoveLamp(LampId lamp);

    /// <summary>How strongly lamps tint characters, 0–<see cref="LightLimits.MaxPeopleLevel"/> (clamped); 0 (the default)
    /// leaves characters to the game's own value. Applies only while one of THIS plugin's lamps is on — see the class
    /// remarks: it is shared and tints every character near any lamp.</summary>
    float PeopleLevel { get; set; }

    /// <summary>Sets <paramref name="person"/>'s key light and rim (<see cref="PersonLight.None"/> puts their look back
    /// exactly). False when lights are not available, the person is gone or has no model, or another plugin lights that
    /// person.</summary>
    /// <param name="person">The entity — you, another player (their posed copy when posed) or an NPC (their stand-in when
    /// posed).</param>
    /// <param name="light">The key light and rim to show.</param>
    bool SetPersonLight(EntityId person, PersonLight light);

    /// <summary>Removes every lamp of this plugin and puts every person it lit back to normal, now.</summary>
    void ResetAll();

    /// <summary>Raised (main thread) after the framework itself ended the lights (a scene end — see the class remarks):
    /// every lamp id is gone and every person is back to normal. Not raised for <see cref="ResetAll"/>.</summary>
    event Action? Released;
}
