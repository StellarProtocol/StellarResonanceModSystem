using System;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Everything a pose model calls, in one record (keeps every constructor small). <paramref name="OnMain"/> runs an
/// action at once on the main thread, else posts it there (the NPC load callback's guard).</summary>
internal sealed record PoseCalls(
    PoseActionCalls Actions, PoseModelCalls Models, PoseSpawnCalls Spawn, LookAtSnapshotReader Looks, PoseLuaQueries Lua,
    Func<Camera?> MainCamera, Action<string> Warn, Action<Action> OnMain);

/// <summary>One posed model. <paramref name="Entity"/> re-fetches the live entity (null once gone) — an entity object is
/// never held across frames (docs/il2cpp-probing-safety.md). <paramref name="Self"/> selects the no-model overloads.</summary>
internal sealed record PoseSubject(Func<object?> Entity, object Model, bool Self, int Gender, float BaseYaw);

/// <summary>The real NPC a generated model stands in for: where it stands and how it faces when the model is requested.</summary>
internal sealed record NpcSource(Func<object?> Entity, object SceneModel, Vector3 Position, Quaternion Rotation, int Gender);
