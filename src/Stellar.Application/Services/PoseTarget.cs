using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// One posable person (spec 2026-10-02 §§ 4.1–4.2). The model is opened by the first control that changes something,
/// never by selection, and only while the game's photo-member limit has room (else <see cref="PoseTargetState.Full"/>;
/// the next control tries again). Every request is recorded in a <see cref="PoseIntent"/>, so an NPC model that is still
/// loading receives all of them, in order, when it arrives, and a reset knows exactly what to undo. A load that arrives
/// after a reset or a release is ignored (generation counter). While the scene freeze is on, the ready model is frozen
/// too, and unfrozen before it is closed. Main thread.
/// </summary>
internal sealed class PoseTarget : IPoseTarget
{
    private const float MaxYaw = 180f;

    private readonly PosingService _svc;
    private readonly PoseIntent _intent = new();
    private IPoseModel? _model;
    private int _generation;
    private bool _opening;
    private bool? _early;
    private bool _lastPlayOk = true;
    private bool _bodyFrozen;

    public PoseTarget(PosingService svc, long uuid, PersonKind kind, object? owner)
    {
        _svc = svc;
        Uuid = uuid;
        Kind = kind;
        Owner = owner;
    }

    public long Uuid { get; }
    public PersonKind Kind { get; }
    public object? Owner { get; }
    public PoseTargetState State { get; private set; } = PoseTargetState.Idle;
    internal PoseTouches Touched => _intent.Touched;

    /// <summary>Takes one of the photo-member slots: a copy or model exists, is on its way, or failed to finish
    /// opening while the backend still handed back a model (a half-made copy can still hold a hidden player, so it
    /// keeps the slot until <see cref="Reset"/> closes it). Not held when the open threw or was refused with
    /// <see cref="DeadPoseModel"/> (no model was ever made — see <c>IPosingBackend.Open</c>) or while
    /// merely <see cref="PoseTargetState.Full"/>.</summary>
    internal bool HoldsModel => _model is not null;

    private bool Usable => _svc.IsAvailable && State is not (PoseTargetState.Failed or PoseTargetState.Released);

    public PoseResult PlayAction(int actionId)
    {
        if (!Usable || actionId <= 0) return PoseResult.Unavailable;
        var wasPaused = State == PoseTargetState.Ready && _intent.Paused;
        _intent.SetAction(actionId);
        if (!Prepare()) return Pending();
        // The game's own play-from-paused path always clears the held persist time before the next play
        // (recon docs/recon/photo-posing-recon.md "Pose play from paused": FreezeFrameCtrl(-1) then ExpressionSinglePlay).
        if (wasPaused) Run(m => { m.SetMoment(-1f); return true; });
        _lastPlayOk = Run(m => m.PlayAction(actionId));
        return _lastPlayOk ? PoseResult.Applied : PoseResult.Refused;
    }

    public float Moment
    {
        get => _intent.Paused ? _intent.Moment
            : State == PoseTargetState.Ready && _intent.ActionId != 0 ? ReadMoment() : -1f;
        set
        {
            if (!Usable || _intent.ActionId == 0) return;
            _intent.SetMoment(value < 0f ? -1f : Math.Clamp(value, 0f, 1f));
            if (Prepare()) Run(m => { m.SetMoment(_intent.Moment); return true; });
        }
    }

    public void SetExpression(int expressionId, bool hold)
    {
        if (!Usable) return;
        var expression = expressionId == 0 ? null : _svc.FindExpression(expressionId);
        if (expressionId != 0 && expression is null) return;
        _intent.SetExpression(expression, hold);
        if (Prepare()) Run(m => { m.SetExpression(expression, hold); return true; });
    }

    public void SetLook(LookPart part, LookMode mode, bool locked)
    {
        if (!Usable) return;
        _intent.SetLook(part, mode, locked);
        if (!Prepare()) return;
        Run(m =>
        {
            m.SetLook(part, mode, locked);
            if (mode == LookMode.Free) m.Aim(part, _intent.AimX(part), _intent.AimY(part));
            return true;
        });
        ReapplyPause();
    }

    public void Aim(LookPart part, float x, float y)
    {
        if (!Usable) return;
        _intent.SetAim(part, Math.Clamp(x, -1f, 1f), Math.Clamp(y, -1f, 1f));
        if (_intent.ModeOf(part) != LookMode.Free || !Prepare()) return;
        Run(m => { m.Aim(part, _intent.AimX(part), _intent.AimY(part)); return true; });
        ReapplyPause();
    }

    public float Yaw
    {
        get => _intent.Yaw;
        set
        {
            if (!Usable) return;
            _intent.SetYaw(Math.Clamp(value, -MaxYaw, MaxYaw));
            if (Prepare()) Run(m => { m.SetYaw(_intent.Yaw); return true; });
        }
    }

    public void Reset() => _svc.ResetTarget(this);

    /// <summary>The visible copy / stand-in's position (the orbit centre); false for you, before Ready and once gone.
    /// Called every frame while orbiting this person: no allocation, failures read as "no position".</summary>
    internal bool TryGetVisiblePosition(out Position3D position)
    {
        position = default;
        if (State != PoseTargetState.Ready || Kind == PersonKind.Self || _model is not { } m) return false;
        try
        {
            if (m.Position is not Position3D p) return false;
            position = p;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Applies or undoes the model freeze (scene freeze on/off). Idempotent; only a ready model.</summary>
    internal void SetFrozen(bool frozen)
    {
        if (State != PoseTargetState.Ready || _bodyFrozen == frozen) return;
        _bodyFrozen = frozen;
        Run(m => { m.SetFrozen(frozen); return true; });
    }

    /// <summary>Re-applies a held pause (after a look change, or after the scene freeze ends).</summary>
    internal void ReapplyPause()
    {
        if (State == PoseTargetState.Ready && _intent.Paused) Run(m => { m.SetMoment(_intent.Moment); return true; });
    }

    /// <summary>Unfreezes and closes the model with what was touched, and forgets every request. A pending NPC load is
    /// ignored. Each step is isolated.</summary>
    internal void Close(PoseTargetState next)
    {
        var model = _model;
        var touched = _intent.Touched;
        var unfreeze = _bodyFrozen;
        _model = null;
        _bodyFrozen = false;
        _generation++;
        _intent.Clear();
        State = next;
        if (model is null) return;
        if (unfreeze) Isolated("unfreezing", () => model.SetFrozen(false));
        Isolated("resetting", () => model.Close(touched));
    }

    /// <summary>True when the model was already ready (apply the change now). Otherwise opens it if needed and there is
    /// room; a model that opens synchronously receives every request — this one included — through the replay.</summary>
    private bool Prepare()
    {
        if (State == PoseTargetState.Ready) return true;
        if (State is PoseTargetState.Idle or PoseTargetState.Full) TryOpen();
        return false;
    }

    private void TryOpen()
    {
        if (_svc.HasRoomFor(this)) { Open(); return; }
        if (State == PoseTargetState.Full) return;
        State = PoseTargetState.Full;
        _svc.RaiseChanged();   // the panel shows "photo limit reached"
    }

    private void Open()
    {
        State = PoseTargetState.Loading;
        var generation = ++_generation;
        _early = null;
        _opening = true;
        try
        {
            var opened = _svc.Backend.Open(Uuid, Kind, ok => OnLoaded(generation, ok));
            _model = opened is DeadPoseModel ? null : opened;   // a refusal made nothing: no model, no member slot
        }
        catch (Exception ex)
        {
            _svc.Warn($"posing: could not prepare {Kind} {Uuid}: {ex.Message}");
            _early = false;
        }
        finally { _opening = false; }
        if (_early is bool ok) Complete(ok);
        else _svc.RaiseChanged();   // the panel shows "loading"
    }

    private void OnLoaded(int generation, bool ok)
    {
        if (generation != _generation || State != PoseTargetState.Loading) return;
        if (_opening) { _early = ok; return; }
        Complete(ok);
    }

    private void Complete(bool ok)
    {
        State = ok && _model is not null ? PoseTargetState.Ready : PoseTargetState.Failed;
        if (State == PoseTargetState.Ready)
        {
            _lastPlayOk = Run(m => _intent.ReplayInto(m));
            if (_svc.Frozen) SetFrozen(true);   // posed during a freeze: freeze with the rest of the scene
        }
        _svc.RaiseChanged();
    }

    private PoseResult Pending() => State switch
    {
        PoseTargetState.Loading => PoseResult.Loading,
        PoseTargetState.Ready => _lastPlayOk ? PoseResult.Applied : PoseResult.Refused,
        PoseTargetState.Full => PoseResult.Full,
        _ => PoseResult.Unavailable,
    };

    private float ReadMoment()
    {
        var value = -1f;
        Run(m => { value = m.ReadMoment(); return true; });
        return value;
    }

    private bool Run(Func<IPoseModel, bool> op)
    {
        if (_model is not { } m) return false;
        try { return op(m); }
        catch (Exception ex)
        {
            _svc.Warn($"posing: {Kind} {Uuid}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void Isolated(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { _svc.Warn($"posing: {what} {Kind} {Uuid} failed: {ex.Message}"); }
    }
}
