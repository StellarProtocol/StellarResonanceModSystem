using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Implements the three plugin-facing party interfaces
/// (<see cref="IPartySnapshot"/>, <see cref="IPartyRoster"/>, <see cref="IPartyEvents"/>)
/// and <see cref="IPartyEventSink"/> (probe-facing write surface). Owns all
/// state; the probe owns none. Mirrors <c>ChatService</c> and
/// <c>CombatService</c> threading model: enqueue from any thread, drain on
/// Unity main.
/// </summary>
internal sealed partial class PartyService : IPartySnapshot, IPartyRoster, IPartyEvents, IPartyEventSink, IDisposable
{
    private readonly ICombatSnapshot _combat;
    private readonly IClientState    _clientState;
    private readonly IPluginLog      _log;

    private readonly ConcurrentQueue<PartyDelta> _pending = new();

    // Reused per-Drain buffers — allocated once and cleared (not reallocated) at
    // the start of each Drain call. Safe because Drain runs exclusively on the
    // Unity main thread (OnGameUpdate postfix is single-threaded) and neither
    // buffer escapes the method: _eventBuffer holds closures over local captures
    // (not over itself), and _charIdSet is cleared before reuse in ApplyFullSnapshot.
    private readonly List<Action>    _eventBuffer = new();
    private readonly HashSet<long>   _charIdSet   = new();

    private readonly Dictionary<long, MemberSlot>     _slots         = new();

    private long      _partyId;
    private long      _leaderCharId;
    private PartyType _partyType  = PartyType.Solo;
    private bool      _isMatching;
    private bool      _isAvailable;
    private long      _localCharId;
    private long      _version;

    private IReadOnlyList<PartyMember>? _membersSnapshot;
    private long                        _membersSnapshotVersion;

    public PartyService(ICombatSnapshot combat, IClientState clientState, IPluginLog log)
    {
        _combat      = combat;
        _clientState = clientState;
        _log         = log;
    }

    public void Dispose() { }

    // === IPartySnapshot + IPartyRoster + IPartyEvents ===

    public bool       IsAvailable  => _isAvailable;
    public bool       IsInParty    => _slots.Count >= 2;
    public long       PartyId      => _partyId;
    public long       LeaderCharId => _leaderCharId;
    public bool       IsLeader     => _leaderCharId != 0 && _leaderCharId == _localCharId;
    public PartyType  PartyType    => _partyType;
    public bool       IsMatching   => _isMatching;

    public IReadOnlyList<PartyMember> Members
    {
        get
        {
            if (_membersSnapshot is not null && _membersSnapshotVersion == _version)
                return _membersSnapshot;

            var list = new List<PartyMember>(_slots.Count);
            foreach (var slot in _slots.Values)
                list.Add(BuildMember(slot));

            list.Sort(static (a, b) =>
            {
                if (a.IsSelf && !b.IsSelf) return -1;
                if (b.IsSelf && !a.IsSelf) return  1;
                return 0;
            });

            _membersSnapshot = list;
            _membersSnapshotVersion = _version;
            return list;
        }
    }

    public PartyMember? Self
    {
        get
        {
            foreach (var m in Members)
                if (m.IsSelf) return m;
            return null;
        }
    }

    public event Action<PartyMember>?                  MemberJoined;
    public event Action<PartyMember, PartyLeaveKind>?  MemberLeft;
    public event Action<PartyMember>?                  MemberUpdated;
    public event Action?                               PartyDissolved;

    // === IPartyEventSink ===

    public void EnqueueFullSnapshot(PartyWireSnapshot s, bool authoritative = false) => _pending.Enqueue(new PartyDelta.FullSnapshot(s, authoritative));
    public void EnqueueMemberFastSync(long c, PartyMemberFastSync d) => _pending.Enqueue(new PartyDelta.MemberFastSync(c, d));
    public void EnqueueMemberSocialSync(long c, PartyMemberSocialSync d) => _pending.Enqueue(new PartyDelta.MemberSocialSync(c, d));
    public void EnqueueMemberLeft(long c, int t)                     => _pending.Enqueue(new PartyDelta.MemberLeft(c, t));
    public void EnqueueGroupLayout(IReadOnlyList<TeamGroupInfo> g)   => _pending.Enqueue(new PartyDelta.GroupLayout(g));
    public void EnqueueDissolve()                                    => _pending.Enqueue(new PartyDelta.Dissolve());

    // === Drain (called from Host's OnGameUpdate postfix on the Unity main thread) ===

    internal void Drain()
    {
        // Derive local char_id from ICombatSnapshot.LocalEntityId once it's known.
        if (_localCharId == 0 && _combat.LocalEntityId != EntityId.None)
        {
            _localCharId = _combat.LocalEntityId.Value >> 16;
            _version++;
        }

        if (_pending.IsEmpty) return;

        _eventBuffer.Clear();

        while (_pending.TryDequeue(out var delta))
        {
            switch (delta)
            {
                case PartyDelta.FullSnapshot s:
                    ApplyFullSnapshot(s.Data, s.Authoritative, _eventBuffer);
                    break;
                case PartyDelta.MemberFastSync ms:
                    ApplyMemberFastSync(ms.CharId, ms.Data, _eventBuffer);
                    break;
                case PartyDelta.MemberSocialSync ss:
                    ApplyMemberSocialSync(ss.CharId, ss.Data, _eventBuffer);
                    break;
                case PartyDelta.MemberLeft ml:
                    ApplyMemberLeft(ml.CharId, ml.LeaveTypeRaw, _eventBuffer);
                    break;
                case PartyDelta.GroupLayout gl:
                    ApplyGroupLayout(gl.Groups, _eventBuffer);
                    break;
                case PartyDelta.Dissolve:
                    ApplyDissolve(_eventBuffer);
                    break;
            }
        }

        if (_eventBuffer.Count > 0) _version++;

        for (var i = 0; i < _eventBuffer.Count; i++)
        {
            try { _eventBuffer[i](); }
            catch (Exception ex) { _log.Warning($"[PartyState] subscriber threw: {ex.Message}"); }
        }

        if (_eventBuffer.Count > 0) DiagRoster();
    }

    // === Apply methods ===
    //
    // DPS aggregation was previously here (PartyService.OnCombatEvent) — it now
    // lives in CombatService keyed by source EntityId. Plugins query
    // ICombatLookup.GetLiveDps(member.EntityId) for any member, including OOA party
    // members (returns 0 when they haven't been observed in the local AOI).
    // See refactor commits 0be283b / 77ae095 / 2696217.

    // Adopt party identity (id / leader / size) ONLY from a snapshot that actually carries one. The periodic
    // empty-roster NoticeUpdateTeamInfo (members=0, no baseInfo → PartyId=0, PartyType=Solo) would otherwise
    // WIPE the identity GetTeamInfoReply set — making a freshly-created/just-joined party flicker back to
    // "Solo"/leaderless between pings (so the 5/20 control never showed on creation, only after a manual size
    // switch). Genuine departure clears identity via the Dissolve delta, not here.
    private void AdoptIdentity(PartyWireSnapshot snap)
    {
        if (snap.PartyId != 0)
        {
            _partyId      = snap.PartyId;
            _leaderCharId = snap.LeaderCharId;
            _partyType    = snap.PartyType;
        }
        _isMatching = snap.IsMatching;
    }

    private void ApplyFullSnapshot(PartyWireSnapshot snap, bool authoritative, List<Action> events)
    {
        _isAvailable = true;
        AdoptIdentity(snap);

        _charIdSet.Clear();

        foreach (var r in snap.Roster)
        {
            _charIdSet.Add(r.CharId);
            if (!_slots.TryGetValue(r.CharId, out var slot))
            {
                slot = new MemberSlot { CharId = r.CharId };
                _slots[r.CharId] = slot;

                MergeRosterIntoSlot(slot, r);
                var joined = BuildMember(slot);
                events.Add(() => MemberJoined?.Invoke(joined));
            }
            else
            {
                var before = BuildMember(slot);
                MergeRosterIntoSlot(slot, r);
                var after = BuildMember(slot);
                if (!before.Equals(after))
                {
                    events.Add(() => MemberUpdated?.Invoke(after));
                }
            }
        }

        // Prune ONLY from an authoritative full fetch (GetTeamInfoReply / CreateTeam_Ret), which carries the
        // COMPLETE roster. Push notifications (NotifyJoinTeam, NoticeUpdateTeamInfo) are additive and ship only
        // a SUBSET — often just the joiner (verified in-world: a join delivered roster=[NekoChan] only). Pruning
        // against those wiped the rest of the party, after which incremental syncs re-created members as bare
        // offline/unnamed slots until the next full fetch. Departures arrive via MemberLeft / Dissolve.
        if (authoritative && snap.Roster.Count > 0) PruneMissing(_charIdSet, events);

        // Apply the raid group/slot layout carried in TeamBaseInfo (so the in-team slot is right at login /
        // first fetch, not only after a NotifyTeamGroupUpdate reposition).
        if (snap.Groups is { Count: > 0 } groups) ApplyGroupLayout(groups, events);
    }

    // Remove slots absent from the (non-empty) snapshot roster, firing MemberLeft.
    private void PruneMissing(HashSet<long> newCharIds, List<Action> events)
    {
        var removed = new List<long>();
        foreach (var existing in _slots.Keys)
            if (!newCharIds.Contains(existing))
                removed.Add(existing);

        foreach (var charId in removed)
        {
            var slot = _slots[charId];
            var snapshotMember = BuildMember(slot);
            _slots.Remove(charId);
            events.Add(() => MemberLeft?.Invoke(snapshotMember, PartyLeaveKind.Unknown));
        }
    }

    private void ApplyMemberFastSync(long charId, PartyMemberFastSync data, List<Action> events)
    {
        _isAvailable = true;

        if (!_slots.TryGetValue(charId, out var slot))
        {
            // Created from an incremental sync (we're receiving live data → the member is online). The wire's
            // own offline flag only rides the authoritative full snapshot, which overrides this on arrival
            // (e.g. a genuinely-away member). Default 0 here left members greyed-out until the party page
            // forced a GetTeamInfoReply.
            slot = new MemberSlot { CharId = charId, OnlineStatusRaw = 1 };
            _slots[charId] = slot;
            ApplyFastFields(slot, data);
            var joined = BuildMember(slot);
            events.Add(() => MemberJoined?.Invoke(joined));
            return;
        }

        bool changed =
            slot.Hp      != data.Hp      ||
            slot.MaxHp   != data.MaxHp   ||
            slot.SceneId != data.SceneId ||
            slot.Position.X != data.Position.X ||
            slot.Position.Y != data.Position.Y ||
            slot.Position.Z != data.Position.Z;

        ApplyFastFields(slot, data);

        if (changed)
        {
            var after = BuildMember(slot);
            events.Add(() => MemberUpdated?.Invoke(after));
        }
    }

    private void ApplyMemberSocialSync(long charId, PartyMemberSocialSync data, List<Action> events)
    {
        _isAvailable = true;

        if (!_slots.TryGetValue(charId, out var slot))
        {
            // Created from an incremental sync (we're receiving live data → the member is online). The wire's
            // own offline flag only rides the authoritative full snapshot, which overrides this on arrival
            // (e.g. a genuinely-away member). Default 0 here left members greyed-out until the party page
            // forced a GetTeamInfoReply.
            slot = new MemberSlot { CharId = charId, OnlineStatusRaw = 1 };
            _slots[charId] = slot;
            ApplySocialFields(slot, data);
            var joined = BuildMember(slot);
            events.Add(() => MemberJoined?.Invoke(joined));
            return;
        }

        // Mirror the guards in ApplySocialFields so we only flag a real, applied change (a partial sync
        // with absent fields — null name, 0 level/profession/group — must not register as a change).
        bool changed =
            (data.Name is not null   && slot.Name       != data.Name)       ||
            (data.Level      > 0     && slot.Level       != data.Level)      ||
            (data.Profession > 0     && slot.Profession  != data.Profession) ||
            (data.GroupId    > 0     && slot.GroupId     != data.GroupId);

        ApplySocialFields(slot, data);

        if (changed)
        {
            var after = BuildMember(slot);
            events.Add(() => MemberUpdated?.Invoke(after));
        }
    }

    // Raid group/slot layout (NotifyTeamGroupUpdate): each group's char_ids are in slot order, so a member's
    // index within its group IS its in-team slot. Update GroupId + Slot for known members; members not present
    // in any group fall back to Slot -1 (handled implicitly — they keep their last group/slot until a sync).
    private void ApplyGroupLayout(IReadOnlyList<TeamGroupInfo> groups, List<Action> events)
    {
        foreach (var g in groups)
        {
            for (var i = 0; i < g.CharIds.Count; i++)
            {
                if (!_slots.TryGetValue(g.CharIds[i], out var slot)) continue;
                if (slot.GroupId == g.GroupId && slot.Slot == i) continue;
                slot.GroupId = g.GroupId;
                slot.Slot = i;
                var after = BuildMember(slot);
                events.Add(() => MemberUpdated?.Invoke(after));
            }
        }
    }

    private void ApplyMemberLeft(long charId, int leaveTypeRaw, List<Action> events)
    {
        // If WE are the one leaving (or being kicked), the party is over for us: the server sends a single
        // NotifyLeaveTeam for self and no further team messages arrive to prune the others. Treat it as a
        // full dissolve so the stale roster doesn't linger in the meter.
        if (charId != 0 && charId == _localCharId) { ApplyDissolve(events); return; }

        if (!_slots.TryGetValue(charId, out var slot)) return;
        var snapshotMember = BuildMember(slot);
        _slots.Remove(charId);

        var kind = MapLeaveKind(leaveTypeRaw);
        events.Add(() => MemberLeft?.Invoke(snapshotMember, kind));
    }

    private void ApplyDissolve(List<Action> events)
    {
        _slots.Clear();
        _partyId      = 0;
        _leaderCharId = 0;
        _partyType    = PartyType.Solo;
        _isMatching   = false;
        events.Add(() => PartyDissolved?.Invoke());
    }

    private static PartyLeaveKind MapLeaveKind(int raw) => raw switch
    {
        1 => PartyLeaveKind.Voluntary,
        2 => PartyLeaveKind.Kicked,
        3 => PartyLeaveKind.Disconnected,
        _ => PartyLeaveKind.Unknown,
    };

    private static void ApplySocialFields(MemberSlot slot, PartyMemberSocialSync data)
    {
        if (data.Name is not null) slot.Name = data.Name;
        if (data.Level      > 0)   slot.Level      = data.Level;
        if (data.Profession > 0)   slot.Profession = data.Profession;
        // Group/slot layout is owned by NotifyTeamGroupUpdate (ApplyGroupLayout). A partial
        // NoticeUpdateTeamMemberInfo social sync carries group_id=0, so only refine upward — never clobber
        // a known raid group back to 0. Clobbering collapsed every team into Group 1 (slot collisions →
        // overflow members dropped from the party-focus grid) whenever the leader invited/moved someone.
        if (data.GroupId    > 0)   slot.GroupId    = data.GroupId;
    }

    private static void ApplyFastFields(MemberSlot slot, PartyMemberFastSync data)
    {
        slot.Hp       = data.Hp;
        slot.MaxHp    = data.MaxHp;
        slot.SceneId  = data.SceneId;
        slot.Position = data.Position;
    }

    private static void MergeRosterIntoSlot(MemberSlot slot, PartyMemberRoster r)
    {
        slot.EnterTimeRaw    = r.EnterTimeRaw;
        slot.OnlineStatusRaw = r.OnlineStatusRaw;
        slot.SceneId         = r.SceneId;
        if (r.GroupId > 0) slot.GroupId = r.GroupId;   // never clobber a known group with a partial 0 (see ApplySocialFields)
        if (r.TalentId > 0)  slot.TalentId = r.TalentId;
        if (r.Social is { } soc)
        {
            if (soc.Name is not null) slot.Name = soc.Name;
            if (soc.Level      > 0)   slot.Level      = soc.Level;
            if (soc.Profession > 0)   slot.Profession = soc.Profession;
        }
        if (r.FastSync is { } fs)
        {
            slot.Hp       = fs.Hp;
            slot.MaxHp    = fs.MaxHp;
            slot.SceneId  = fs.SceneId;
            slot.Position = fs.Position;
        }
    }

    // === Helpers ===

    private PartyMember BuildMember(MemberSlot s) => new PartyMember(
        CharId:       s.CharId,
        Name:         s.Name,
        Profession:   s.Profession,
        Level:        s.Level,
        Hp:           s.Hp,
        MaxHp:        s.MaxHp,
        SceneId:      s.SceneId,
        Position:     s.Position,
        IsOnline:     s.IsOnline,
        IsSelf:       s.CharId != 0 && s.CharId == _localCharId,
        GroupId:      s.GroupId,
        Slot:         s.Slot,
        Talent:       s.TalentId);
}
