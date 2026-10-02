using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Application.Tests.FreeCamera;

namespace Stellar.Application.Tests.Posing;

internal sealed class FakePoseModel : IPoseModel
{
    public readonly List<string> Calls = new();
    public bool Refuse;
    public float Live = 0.25f;
    public Position3D? Visible = new Position3D(3, 0, 4);
    public PoseTouches? Closed;
    public int CloseCount;
    public Action? OnFrozenCalled;
    public Position3D? Position => Visible;
    public void SetFrozen(bool frozen) { Calls.Add($"frozen {frozen}"); OnFrozenCalled?.Invoke(); }
    private static string N(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    public bool PlayAction(int actionId) { Calls.Add($"play {actionId}"); return !Refuse; }
    public void SetMoment(float fraction) => Calls.Add($"moment {N(fraction)}");
    /// <summary>The action the model reports as running (with <see cref="Live"/> as its progress; Live &lt; 0 = none).</summary>
    public int RunningId = 9020;
    public PoseActionReading ReadAction() { Calls.Add("read"); return Live < 0f ? PoseActionReading.None : new PoseActionReading(RunningId, Live); }
    public void SetExpression(ExpressionInfo? expression, bool hold) => Calls.Add($"face {expression?.Id ?? 0} hold={hold}");
    public void SetLook(LookPart part, LookMode mode, bool locked) => Calls.Add($"look {part} {mode} lock={locked}");
    public void Aim(LookPart part, float x, float y) => Calls.Add($"aim {part} {N(x)},{N(y)}");
    public void SetYaw(float offsetDegrees) => Calls.Add($"yaw {offsetDegrees.ToString("0", CultureInfo.InvariantCulture)}");
    public void Close(PoseTouches touched) { Calls.Add($"close {touched}"); Closed = touched; CloseCount++; }
}

internal sealed class FakePosingBackend : IPosingBackend
{
    public readonly Dictionary<long, PersonKind> Kinds = new()
    {
        [1] = PersonKind.Self, [2] = PersonKind.Player, [4] = PersonKind.Player, [5] = PersonKind.Player,
        [3] = PersonKind.Npc, [6] = PersonKind.Npc, [7] = PersonKind.Npc,
    };
    public readonly List<(long Uuid, PersonKind Kind, FakePoseModel Model)> Opened = new();
    public readonly List<Action<bool>> PendingLoads = new();
    public List<ExpressionInfo> ExpressionList = new() { new(1003, "Angry", 303, 403), new(1015, "Startled", 315, 415) };
    public bool AsyncNpc = true, FailOpen, RefuseActions, RefuseOpen;
    public bool ThrowOnReadExpressions, ThrowOnMemberLimit;
    public int ExpressionReads, Limit = 30, LimitReads;
    /// <summary>What each live person is doing (their own model); absent = idle.</summary>
    public readonly Dictionary<long, PoseActionReading> LiveActions = new();
    public int LiveActionReads;
    public bool ThrowOnReadAction;
    public event Action<long>? PersonRemoved;

    public PoseActionReading ReadAction(long uuid)
    {
        LiveActionReads++;
        if (ThrowOnReadAction) throw new InvalidOperationException("the model is gone");
        return LiveActions.TryGetValue(uuid, out var r) ? r : PoseActionReading.None;
    }

    public PersonKind? KindOf(long uuid) => Kinds.TryGetValue(uuid, out var k) ? k : null;

    public void People(float radius, List<PersonInfo> into)
    {
        into.Add(new PersonInfo(new EntityId(1), "Revette", PersonKind.Self, 0f));
        into.Add(new PersonInfo(new EntityId(2), "Celia", PersonKind.Player, 3f));
    }

    public IReadOnlyList<ExpressionInfo> ReadExpressions()
    {
        ExpressionReads++;
        if (ThrowOnReadExpressions) throw new InvalidOperationException("the Lua VM is not ready");
        return ExpressionList;
    }

    public int MemberLimit()
    {
        LimitReads++;
        if (ThrowOnMemberLimit) throw new InvalidOperationException("the Lua VM is not ready");
        return Limit;
    }

    public IPoseModel Open(long uuid, PersonKind kind, Action<bool> loaded)
    {
        if (RefuseOpen) { loaded(false); return DeadPoseModel.Instance; }   // e.g. the scene-change settle window
        var m = new FakePoseModel { Refuse = RefuseActions };
        Opened.Add((uuid, kind, m));
        if (kind == PersonKind.Npc && AsyncNpc) PendingLoads.Add(loaded);
        else loaded(!FailOpen);
        return m;
    }

    public void Remove(long uuid) => PersonRemoved?.Invoke(uuid);
    public FakePoseModel Model(long uuid) => Opened.FindLast(o => o.Uuid == uuid).Model;
}

internal sealed class FakeFreezeSignal : ISceneFreeze
{
    public bool IsFrozen { get; private set; }
    public bool HoldsPositions => false;
    public event Action<bool>? Changed;
    public IDisposable Freeze() => throw new NotSupportedException();
    public void Raise(bool frozen) { IsFrozen = frozen; Changed?.Invoke(frozen); }
}

/// <summary>A posing service over a real camera arbiter (fake camera backend), with the camera held.</summary>
internal sealed class PosingRig
{
    public readonly List<string> Warnings = new();
    public readonly CameraOverrideServiceTests.FakeBackend CameraBackend = new();
    public readonly CameraOverrideService Camera;
    public readonly FakePosingBackend Backend = new();
    public readonly FakeFreezeSignal Freeze = new();
    public readonly PosingService Svc;
    public readonly object CameraOwner = new();
    public ICameraControl? Control;

    /// <summary>Fake monotonic clock (ms) fed to <see cref="PosingService"/>'s retry latches (review F1/F2) — advance it
    /// to simulate the 5 s retry window elapsing.</summary>
    public long NowMs;

    public PosingRig(bool acquire = true)
    {
        Camera = new CameraOverrideService(CameraBackend, new LookAtService(new CameraOverrideServiceTests.FakeLookAt(), Warnings.Add), false, Warnings.Add);
        Svc = new PosingService(Backend, Camera, Freeze, Warnings.Add, () => NowMs);
        if (acquire) Acquire();
    }

    public void Acquire() => Camera.TryAcquire(CameraOwner, out Control);

    public IPoseTarget Target(long uuid) => Svc.Select(new EntityId(uuid))!;
}
