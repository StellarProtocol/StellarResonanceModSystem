using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// Posing by person (spec 2026-10-02). People are targets only while a free camera is held. Every camera release —
/// whatever the reason (exit, zone change, cutscene, the game's camera mode, disconnect, unload, a frame-handler error) —
/// resets every touched person; a despawning person is released alone; a plugin's people are released by its facade on
/// unload. Copies and NPC models are capped at the game's own photo-member limit. The scene freeze also freezes posed
/// models (they are not entities, so the scene freeze never sees them) and, when it ends, unfreezes them and re-applies
/// held pauses so the unfreeze never resumes a pose (§ 4.5). Main thread.
/// </summary>
internal sealed class PosingService : IPosing
{
    /// <summary>The PC value of <c>PhotographTeamMemberLimit</c> in release_3.7's Global table (<c>{30, 10}</c>), used
    /// until the live value can be read.</summary>
    internal const int DefaultMemberLimit = 30;

    // A Lua-backed read (expressions, the member limit) that comes back empty/zero or throws is retried at most this
    // often — an empty answer IS an answer (no unlocked expressions, or the VM genuinely isn't ready yet); without a
    // latch the panel's 10 Hz poll re-runs the Lua query, and re-warns on a throw, on every single frame (review F1/F2).
    // Same pattern as GameVisibilityBackend's negative probes (docs/il2cpp-probing-safety.md § negative-cache races).
    private const long RetryTtlMs = 5_000;

    private readonly CameraOverrideService _camera;
    private readonly Action<string> _warn;
    private readonly Dictionary<long, PoseTarget> _targets = new();
    private readonly List<PersonInfo> _people = new();
    private readonly NegativeProbeCache _expressionsRetry;
    private readonly NegativeProbeCache _memberLimitRetry;
    private IReadOnlyList<ExpressionInfo> _expressions = Array.Empty<ExpressionInfo>();
    private int _memberLimit;
    private bool _frozen;

    public PosingService(IPosingBackend backend, CameraOverrideService camera, ISceneFreeze freeze, Action<string> warn, Func<long>? nowMs = null)
    {
        Backend = backend;
        _camera = camera;
        _warn = warn;
        _frozen = freeze.IsFrozen;
        var clock = nowMs ?? (() => Environment.TickCount64);
        _expressionsRetry = new NegativeProbeCache(clock, RetryTtlMs);
        _memberLimitRetry = new NegativeProbeCache(clock, RetryTtlMs);
        camera.Released += _ => ResetAll();
        freeze.Changed += OnFreezeChanged;
        backend.PersonRemoved += OnPersonRemoved;
    }

    internal IPosingBackend Backend { get; }

    /// <summary>True while any person is selected (the diagnostics send tap logs only then).</summary>
    internal bool HasTargets => _targets.Count > 0;

    /// <summary>The scene freeze is on (posed models freeze with it).</summary>
    internal bool Frozen => _frozen;

    /// <summary>The game's photo-member limit: the live value once readable (then kept), <see cref="DefaultMemberLimit"/>
    /// until then.</summary>
    internal int MemberLimit
    {
        get
        {
            if (_memberLimit > 0) return _memberLimit;
            if (_memberLimitRetry.IsSuppressed) return DefaultMemberLimit;
            var read = 0;
            try { read = Backend.MemberLimit(); }
            catch (Exception ex)
            {
                if (_memberLimitRetry.MarkNegative()) _warn("posing: could not read the photo-member limit: " + ex.Message);
                return DefaultMemberLimit;
            }
            if (read <= 0)
            {
                _memberLimitRetry.MarkNegative();
                return DefaultMemberLimit;
            }
            _memberLimitRetry.MarkRecovered();
            _memberLimit = read;
            return read;
        }
    }

    public bool IsAvailable => _camera.IsOverridden;

    public event Action? Changed;

    public IReadOnlyList<ExpressionInfo> Expressions
    {
        get
        {
            if (_expressions.Count > 0) return _expressions;
            if (_expressionsRetry.IsSuppressed) return _expressions;
            try { _expressions = Backend.ReadExpressions(); }
            catch (Exception ex)
            {
                if (_expressionsRetry.MarkNegative()) _warn("posing: could not read the expressions: " + ex.Message);
                return _expressions;
            }
            if (_expressions.Count > 0) _expressionsRetry.MarkRecovered();
            else _expressionsRetry.MarkNegative();
            return _expressions;
        }
    }

    public IReadOnlyList<PersonInfo> NearbyPeople(float radius)
    {
        if (!IsAvailable) return Array.Empty<PersonInfo>();
        _people.Clear();
        try { Backend.People(radius, _people); }
        catch (Exception ex)
        {
            _warn("posing: could not list the people nearby: " + ex.Message);
            _people.Clear();
        }
        return _people.ToArray();
    }

    public IPoseTarget? Select(EntityId person) => Select(person, owner: null);

    internal IPoseTarget? Select(EntityId person, object? owner)
    {
        if (!IsAvailable || person.IsNone) return null;
        if (_targets.TryGetValue(person.Value, out var known)) return Equals(known.Owner, owner) ? known : null;
        PersonKind? kind;
        try { kind = Backend.KindOf(person.Value); }
        catch (Exception ex)
        {
            _warn("posing: could not read a person: " + ex.Message);
            return null;
        }
        if (kind is not PersonKind k) return null;
        var target = new PoseTarget(this, person.Value, k, owner);
        _targets[person.Value] = target;
        return target;
    }

    public bool TryGetVisiblePosition(EntityId person, out Position3D position)
    {
        position = default;
        return _targets.Count > 0 && _targets.TryGetValue(person.Value, out var t) && t.TryGetVisiblePosition(out position);
    }

    public void ResetAll() => CloseWhere(_ => true);

    internal void ReleaseOwner(object owner) => CloseWhere(t => Equals(t.Owner, owner));

    internal void ResetTarget(PoseTarget target)
    {
        if (!_targets.TryGetValue(target.Uuid, out var known) || !ReferenceEquals(known, target)) return;
        target.Close(PoseTargetState.Idle);
        RaiseChanged();
    }

    /// <summary>The game's own cap (camera_member_vm AddMemberToList / UpdateMemberListData): the member list holds you
    /// plus at most limit − 1 other players; NPC partners have their own list of limit. You are always posable.</summary>
    internal bool HasRoomFor(PoseTarget target)
    {
        if (target.Kind == PersonKind.Self) return true;
        var cap = target.Kind == PersonKind.Player ? MemberLimit - 1 : MemberLimit;
        var used = 0;
        foreach (var t in _targets.Values)
            if (!ReferenceEquals(t, target) && t.Kind == target.Kind && t.HoldsModel) used++;
        return used < cap;
    }

    internal ExpressionInfo? FindExpression(int id)
    {
        foreach (var e in Expressions)
            if (e.Id == id) return e;
        return null;
    }

    internal void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _warn("posing: a Changed handler threw: " + ex.Message); }
    }

    internal void Warn(string message) => _warn(message);

    private void CloseWhere(Func<PoseTarget, bool> match)
    {
        var gone = _targets.Values.Where(match).ToList();
        if (gone.Count == 0) return;
        foreach (var t in gone)
        {
            _targets.Remove(t.Uuid);
            t.Close(PoseTargetState.Released);
        }
        RaiseChanged();
    }

    // Runs on EVERY entity removal (the RemoveEntity prefix): nothing selected = return at once.
    private void OnPersonRemoved(long uuid)
    {
        if (_targets.Count == 0 || !_targets.TryGetValue(uuid, out var t)) return;
        _targets.Remove(uuid);
        t.Close(PoseTargetState.Released);
        RaiseChanged();
    }

    // ISceneFreeze.Changed also fires when only HoldsPositions flips (argument = IsFrozen): act on real flips only.
    private void OnFreezeChanged(bool frozen)
    {
        if (frozen == _frozen) return;
        _frozen = frozen;
        // Snapshot (like CloseWhere): SetFrozen runs backend code and can RaiseChanged (PersonRemoved, a load
        // completing); a Changed subscriber reacting by Select()-ing a new person would otherwise insert into
        // _targets mid-enumeration (Dictionary<,> throws on the next MoveNext after an insert during a foreach).
        foreach (var t in _targets.Values.ToList()) t.SetFrozen(frozen);
        if (!frozen) ReapplyPauses();
    }

    // Review F3 (verified, not stripped): ReapplyPause() runs the same shape of backend call as SetFrozen above
    // (PoseTarget.Run -> IPoseModel.SetMoment) — opaque game/model code that can itself raise RaiseChanged
    // (PersonRemoved, a load completing) and so can just as well drive a reentrant Select() insert into _targets.
    // Kept as a snapshot for the same reason as OnFreezeChanged; do not drop it without re-proving no call in the
    // loop can mutate _targets.
    private void ReapplyPauses()
    {
        foreach (var t in _targets.Values.ToList()) t.ReapplyPause();
    }
}
