using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Pose people while a free camera is held (<see cref="ICameraOverride"/>), like the game's own photo panel: the local
/// player live, other players as a local photo copy (the real player hidden), NPCs as a locally generated model (the
/// real NPC hidden and never changed). Everything is local: copies and generated models send nothing. The copy or model
/// is made on the first control that changes something, never on <see cref="Select"/>. When the free camera ends — for
/// any reason — the framework resets every touched person (action, expression, look, facing; copies and models removed,
/// real people shown again); a person who leaves is released alone; a plugin's people are released when it unloads.
/// At most the game's own photo-member limit of copies (you plus limit − 1 players) and of NPC models exist at once;
/// beyond it a target reports <see cref="PoseTargetState.Full"/>. While the scene freeze (<see cref="ISceneFreeze"/>) is
/// on, posed copies and models freeze too. Main thread only.
/// </summary>
public interface IPosing
{
    /// <summary>True while a free camera is held — the only time people can be posed.</summary>
    bool IsAvailable { get; }

    /// <summary>The people within <paramref name="radius"/> m of the local player: the local player first, then players
    /// and NPCs by distance. Reads the game once per call (allocates) — call it on a user action, not every frame.
    /// Empty while <see cref="IsAvailable"/> is false.</summary>
    /// <param name="radius">Search radius in metres.</param>
    IReadOnlyList<PersonInfo> NearbyPeople(float radius);

    /// <summary>The game's facial expressions, in the game's order. Empty until the player is in the world.</summary>
    IReadOnlyList<ExpressionInfo> Expressions { get; }

    /// <summary>The pose target for <paramref name="person"/> (the same object while it lives), or null when posing is
    /// not available, the entity is not a player or an NPC, or another plugin is posing that person.</summary>
    /// <param name="person">The entity to pose.</param>
    IPoseTarget? Select(EntityId person);

    /// <summary>Where the visible copy or stand-in of <paramref name="person"/> stands — what a camera orbiting them should
    /// centre on while the real person is hidden (and may walk away). False for the local player (posed live), for a
    /// person not posed yet or still loading, and once released. No allocation: safe to call every frame.</summary>
    /// <param name="person">The entity selected for posing.</param>
    /// <param name="position">The posed model's world position.</param>
    bool TryGetVisiblePosition(EntityId person, out Position3D position);

    /// <summary>Resets every person this plugin touched, now (the free camera stays on).</summary>
    void ResetAll();

    /// <summary>Raised (main thread) when a target's <see cref="IPoseTarget.State"/> changed: a model finished loading or
    /// failed, a person was reset or released.</summary>
    event Action? Changed;
}

/// <summary>One person being posed. Calls on a <see cref="PoseTargetState.Released"/> target do nothing.</summary>
public interface IPoseTarget
{
    /// <summary>Where this target stands.</summary>
    PoseTargetState State { get; }

    /// <summary>Plays an action (an emote id from <see cref="IEmotes.Unlocked"/>) on this person, without telling other
    /// players. Makes the copy / model on first use.</summary>
    /// <param name="actionId">The emote/action id.</param>
    PoseResult PlayAction(int actionId);

    /// <summary>Set: 0–1 holds the current action at that point of its length; −1 lets it play. Get: the held point while
    /// held; while playing, how far it has played (reads the game — call on a user action, not every frame); −1 when
    /// nothing plays. Ignored before <see cref="PlayAction"/>.</summary>
    float Moment { get; set; }

    /// <summary>Shows an expression (an <see cref="ExpressionInfo.Id"/>; 0 clears). With <paramref name="hold"/> it stays
    /// until changed; without, it fades after a few seconds as in the game.</summary>
    /// <param name="expressionId">Expression id, or 0 for none.</param>
    /// <param name="hold">Keep it until changed.</param>
    void SetExpression(int expressionId, bool hold);

    /// <summary>Sets where the head or the eyes look. <paramref name="locked"/> pins a <see cref="LookMode.Lens"/> target
    /// where the camera is now (the camera can then move away).</summary>
    /// <param name="part">Head or eyes.</param>
    /// <param name="mode">Default, Lens or Free.</param>
    /// <param name="locked">Pin the Lens target.</param>
    void SetLook(LookPart part, LookMode mode, bool locked);

    /// <summary>The <see cref="LookMode.Free"/> point: <paramref name="x"/> right / <paramref name="y"/> up, each −1..1,
    /// around the head as the camera sees it. Remembered in any mode; applied in Free.</summary>
    /// <param name="part">Head or eyes.</param>
    /// <param name="x">−1 (left) … 1 (right).</param>
    /// <param name="y">−1 (down) … 1 (up).</param>
    void Aim(LookPart part, float x, float y);

    /// <summary>Degrees (−180…180) turned from the facing the person had when first posed.</summary>
    float Yaw { get; set; }

    /// <summary>Returns this person to normal now (removes their copy or model and shows the real person); the target
    /// stays selected and the next control starts fresh.</summary>
    void Reset();
}
