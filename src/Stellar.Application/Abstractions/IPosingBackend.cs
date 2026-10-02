using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Which parts of a person a pose changed — what a reset must undo (spec 2026-10-02 § 4.2).</summary>
[Flags]
internal enum PoseTouches
{
    None = 0,
    Action = 1,
    Expression = 2,
    Head = 4,
    Eyes = 8,
    Yaw = 16,
}

/// <summary>What a person or a posed model is doing: the game's action id (<c>GetLuaAttrActionInfoActionId</c>) and how
/// far it has played, 0–1 (<c>PassedTime / TotalTime</c>; 0 when the length is unknown). <see cref="None"/> = idle,
/// gone or unreadable.</summary>
internal readonly record struct PoseActionReading(int ActionId, float Moment)
{
    /// <summary>Nothing playing (or nothing readable).</summary>
    public static readonly PoseActionReading None = new(0, -1f);

    /// <summary>An action is running.</summary>
    public bool IsPlaying => ActionId > 0;
}

/// <summary>Game side of <c>IPosing</c> (recon docs/recon/photo-posing-recon.md, runs 4–5). Main thread.</summary>
internal interface IPosingBackend
{
    /// <summary>Self / player / NPC for a live entity; null when it is gone or is not a person.</summary>
    PersonKind? KindOf(long uuid);

    /// <summary>Fills <paramref name="into"/> with the people within <paramref name="radius"/> m of the local player, the
    /// local player first, then by distance. A one-shot read (allocates); never per frame.</summary>
    void People(float radius, List<PersonInfo> into);

    /// <summary>The game's facial expressions; empty until the Lua VM can answer.</summary>
    IReadOnlyList<ExpressionInfo> ReadExpressions();

    /// <summary>The game's photo-member limit (<c>Z.Global.PhotographTeamMemberLimit</c> as camera_member_vm reads it —
    /// 30 on the PC UI in release_3.7's table); 0 when it cannot be read yet.</summary>
    int MemberLimit();

    /// <summary>Prepares <paramref name="uuid"/>: the live model (self), a photo copy with the real player hidden
    /// (player), or a generated model with the real NPC hidden (NPC). <paramref name="loaded"/> runs exactly once —
    /// inside this call for self and players, later (async load) for NPCs — with false when it failed. On throw, or
    /// when it returns <see cref="DeadPoseModel.Instance"/>, nothing is hidden or loading: the caller treats the open as
    /// failed and has no model to close (and the person holds no photo-member slot).</summary>
    IPoseModel Open(long uuid, PersonKind kind, Action<bool> loaded);

    /// <summary>The action the live person <paramref name="uuid"/> is doing (their own model, not a copy);
    /// <see cref="PoseActionReading.None"/> when idle, gone, destroying, or inside the scene-change settle window. Polled
    /// by panels (~10 Hz): compiled accessors, no allocation, never throws.</summary>
    PoseActionReading ReadAction(long uuid);

    /// <summary>Raised (main thread) just before the game removes an entity.</summary>
    event Action<long>? PersonRemoved;
}

/// <summary>The posed model as a body in the world — all the orbit and the scene freeze need. Split from
/// <see cref="IPoseModel"/> (the controls) so each side keeps one responsibility.</summary>
internal interface IPoseBody
{
    /// <summary>The visible world position of a copy / generated model; null for the live local player (the camera follows
    /// the entity) or once the model is gone. Read every frame while orbiting: no allocation.</summary>
    Position3D? Position { get; }

    /// <summary>The freeze stage that works on a bare model (recon run 2/3 stage 2: drawn speed 0, the prior kept); false
    /// restores it. A no-op for the live local player, whom the scene freeze already covers as an entity.</summary>
    void SetFrozen(bool frozen);
}

/// <summary>One opened person: the controls. Every call is a no-op once the model is gone. Main thread.</summary>
internal interface IPoseModel : IPoseBody
{
    /// <summary>Plays an action without telling the server; false when the game's own check refuses it. The caller
    /// clears a held moment (<see cref="SetMoment"/> -1) first when the person was paused — the game's own
    /// play-from-paused path always resets the persist time before the next <c>PlayAction</c> (recon
    /// docs/recon/photo-posing-recon.md "Pose play from paused").</summary>
    bool PlayAction(int actionId);

    /// <summary>Holds the current action at <paramref name="fraction"/> (0–1) of its length; −1 lets it play. When nothing
    /// was played through <see cref="PlayAction"/>, the action the model is already doing (a person's own emote, or the
    /// one a photo copy inherited) is the one held — it is never restarted.</summary>
    void SetMoment(float fraction);

    /// <summary>What this model is doing now (whoever started it) and how far it has played;
    /// <see cref="PoseActionReading.None"/> when nothing plays or the model is gone.</summary>
    PoseActionReading ReadAction();

    /// <summary>Shows <paramref name="expression"/> (null clears it); <paramref name="hold"/> keeps it until changed.</summary>
    void SetExpression(ExpressionInfo? expression, bool hold);

    /// <summary>Head or eye look mode; <paramref name="locked"/> pins a Lens target where the camera is now. Free opens
    /// the look only — <see cref="Aim"/> places the point.</summary>
    void SetLook(LookPart part, LookMode mode, bool locked);

    /// <summary>Free look point: <paramref name="x"/>/<paramref name="y"/> in −1..1 around the head, camera-relative.</summary>
    void Aim(LookPart part, float x, float y);

    /// <summary>Turns the person <paramref name="offsetDegrees"/> from the facing they had when opened.</summary>
    void SetYaw(float offsetDegrees);

    /// <summary>Undoes <paramref name="touched"/> on a live model, or removes a copy / generated model and shows the real
    /// person again. Safe while an NPC model is still loading (it is removed when it arrives). Idempotent.</summary>
    void Close(PoseTouches touched);
}
