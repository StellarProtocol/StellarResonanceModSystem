using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Coll = Stellar.Infrastructure.Game.FreezeDiagCollections.Coll;
namespace Stellar.Infrastructure.Game;

/// <summary>The combat-freeze sampler proper (diagnostics only): the press census, and every 0.5 s the nearest ≤ 20
/// monsters / bosses within 40 m of the camera or the local player, each read through <see cref="FreezeDiagReader"/>
/// (liveness-gated, fresh lookup per sample). Runs on the late frame BEFORE our hold write, so the drawn position read is
/// what the game left there since our last write. Lines: <c>census:</c> (press), <c>e</c> (one entity), <c>sample</c>
/// (per-sample totals). Anomalous entities are logged first, the rest round-robin, ≤ 8 entity lines per sample.</summary>
internal sealed partial class GameFreezeBackend
{
    private const int CandidateCap = 128;
    private readonly HashSet<long> _diagMonsterTargets = new();
    private readonly HashSet<long> _diagAppeared = new();
    private readonly int[] _diagTake = new int[FreezeDiagCounters.SlotCount];

    private sealed class DiagCand
    {
        public long Uuid;
        public object Entity = null!;
        public int Kind;
        public float Dist;
        public string Colls = "";
        public readonly FreezeDiagReading R = new();
        public FreezeDiagRow Row = null!;
        public int Priority;
        public string Line = "";
    }

    private void DiagCensus()
    {
        var colls = _diagColls!;
        colls.Refresh();
        _diagMonsterTargets.Clear();
        _diagAppeared.Clear();
        var kinds = new SortedDictionary<int, int>();
        foreach (var uuid in _ids)
        {
            var k = _entities.Kind(uuid);
            kinds[k] = kinds.TryGetValue(k, out var n) ? n + 1 : 1;
            if (k == FreezeKinds.Monster && _diagMonsterTargets.Add(uuid)) DiagWatch(uuid);
        }
        var dict = new List<long>();
        colls.Keys(Coll.Monster, dict, 512);
        colls.Keys(Coll.Boss, dict, 512);
        var missing = dict.Distinct().Where(u => !_diagTargets.Contains(u)).ToList();
        foreach (var u in missing) DiagWatch(u);
        _log.Info($"{DiagTag}census: entityDict={colls.Count(Coll.Entity)} monsterDict={colls.Count(Coll.Monster)} " +
                  $"bossDict={colls.Count(Coll.Boss)} delayRemove={colls.Count(Coll.DelayRemove)} clientDict={colls.Count(Coll.Client)} " +
                  $"targets={_ids.Count} targetMonsters={_diagMonsterTargets.Count} monstersNotTargeted={missing.Count} " +
                  $"[{string.Join(",", missing.Take(12))}] kinds={{{string.Join(",", kinds.Select(kv => $"{kv.Key}:{kv.Value}"))}}} " +
                  $"self={_ledger.Self} selfPos={V(_entities.LocalPlayerPosition())} cam={V(CameraPos())} hold={_holding} held={_held.Count} " +
                  $"gateTracked={_speedGate.Tracked} watched={_diagCounters!.Watched}");
    }

    /// <summary>Starts per-entity counting for a monster and maps its anim component / controller / drawn-transform
    /// pointers to it (so hooks that only see an instance can name it).</summary>
    private void DiagWatch(long uuid)
    {
        if (!_diagCounters!.Watch(uuid) || _entities.EntityByUuid(uuid) is not { } e) return;
        var r = new FreezeDiagReading();
        _diagReader!.Read(e, r);
        DiagMap(uuid, r, e);
    }

    private void DiagMap(long uuid, FreezeDiagReading r, object entity)
    {
        var c = _diagCounters!;
        c.Map(FreezeDiagReader.Ptr(entity), uuid);
        c.Map(r.Comp, uuid);
        c.Map(r.Controller, uuid);
        c.Map(r.Go, uuid);
        c.Map(r.Model, uuid);
    }

    private void DiagSample(long now)
    {
        _diagColls!.Refresh();
        var cands = DiagCandidates();
        Array.Clear(_diagS, 0, _diagS.Length);
        foreach (var c in cands) DiagReadOne(c);
        var logged = 0;
        foreach (var c in cands.OrderByDescending(c => c.Priority).ThenBy(c => c.Row.LastLogged))
        {
            if (logged >= FreezeDiagClock.EntityLinesPerSample || !_diagClock!.TakeLine(now)) break;
            c.Row.LastLogged = _diagClock.Samples;
            _log.Info(c.Line);
            logged++;
        }
        if (_diagClock!.TakeLine(now)) _log.Info(DiagSampleLine(now, cands, logged));
        if (_diagClock.Samples % 2 == 1) DiagFxCensus(now);
    }

    private List<DiagCand> DiagCandidates()
    {
        var ids = new HashSet<long>(_diagMonsterTargets);
        ids.UnionWith(_diagAppeared);
        var keys = new List<long>();
        foreach (var coll in new[] { Coll.Monster, Coll.Boss, Coll.DelayRemove, Coll.Client }) _diagColls!.Keys(coll, keys, CandidateCap);
        ids.UnionWith(keys);
        var cam = CameraPos();
        var self = _entities.LocalPlayerPosition();
        var list = new List<DiagCand>();
        foreach (var uuid in ids)
        {
            if (list.Count >= CandidateCap) break;
            if (DiagCandidate(uuid, cam, self) is { } c) list.Add(c);
        }
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        if (list.Count > FreezeDiagClock.MaxEntities) list.RemoveRange(FreezeDiagClock.MaxEntities, list.Count - FreezeDiagClock.MaxEntities);
        return list;
    }

    private DiagCand? DiagCandidate(long uuid, Vector3? cam, Vector3? self)
    {
        try
        {
            var e = _entities.EntityByUuid(uuid) ?? _diagColls!.DelayRemoved(uuid);
            if (e is null) return null;
            var kind = _entities.EntType(e);
            var colls = _diagColls!.Flags(uuid);
            if (kind != FreezeKinds.Monster && !colls.Contains('M') && !colls.Contains('B')) return null;
            if (_entities.LiveModel(e) is not { } m || _entities.AttrPosition(m) is not { } at) return null;
            var d = Math.Min(cam is { } c ? Vector3.Distance(at, c) : float.MaxValue, self is { } s ? Vector3.Distance(at, s) : float.MaxValue);
            if (d > FreezeDiagClock.Radius) return null;
            return new DiagCand { Uuid = uuid, Entity = e, Kind = kind, Dist = d, Colls = colls };
        }
        catch { return null; }   // despawned mid-read: not a candidate this sample
    }

    private void DiagReadOne(DiagCand c)
    {
        _diagReader!.Read(c.Entity, c.R);
        if (!_diagRows.TryGetValue(c.Uuid, out var row)) _diagRows[c.Uuid] = row = new FreezeDiagRow(c.Uuid, c.Kind) { FirstColls = c.Colls };
        c.Row = row;
        if (_diagCounters!.Watch(c.Uuid)) DiagMap(c.Uuid, c.R, c.Entity);
        var swap = row.NotePointers(c.R.Model, c.R.Comp, c.R.Controller, c.R.Go);
        row.Samples++;
        DiagScore(c, swap);
    }

    private void DiagScore(DiagCand c, bool swap)
    {
        var r = c.R;
        var row = c.Row;
        var tgt = _diagTargets.Contains(c.Uuid) ? "press" : _diagAppeared.Contains(c.Uuid) ? "appear" : "none";
        var st1 = _ledger.Factors.ContainsKey(c.Uuid);
        var st2 = _ledger.Speeds.ContainsKey(c.Uuid);
        var heldEntry = _held.Find(c.Uuid);
        var heldAt = heldEntry is null ? -1 : 0;
        var gate = _speedGate.TracksFor(r.Comp, c.Uuid);
        var holdLive = heldAt >= 0 && _entities.EntityByUuid(c.Uuid) is not null;   // the hold only reaches GetEntity's entities
        var stage = _diagSink!.LastStage.TryGetValue(c.Uuid, out var ls) ? $"{ls.Effect}:{ls.Stage}" : "-";
        var dHold = heldAt >= 0 && r.DrawnPos is { } dp ? Vector3.Distance(dp, heldEntry!.Value.Pos) : float.NaN;
        var drawnAnim = r.Drawn > FreezeLedger.SpeedEpsilon;
        var ctlAnim = !drawnAnim && r.ControllerSpeed > FreezeLedger.SpeedEpsilon;
        var offHold = dHold > 0.05f;
        row.Untargeted |= tgt == "none";
        row.NeverFrozen |= tgt != "none" && !st1 && !st2;
        row.NotHeld |= heldAt < 0;
        row.Untracked |= !gate;
        if (drawnAnim) row.DrawnAnimating++;
        if (ctlAnim) row.ControllerAnimating++;
        if (offHold) { row.OffHold++; row.MaxOffHold = Math.Max(row.MaxOffHold, dHold); }
        _diagCounters!.Take(c.Uuid, _diagTake);
        var hits = DiagHitsText(_diagTake);
        var fix = DiagFixCheck(c, heldEntry, out var fixOff);   // .FixCheck.Diagnostics.cs: rotation + ECS layer gate
        c.Priority = (tgt == "none" ? 8 : 0) + (drawnAnim ? 4 : 0) + (ctlAnim ? 4 : 0) + (offHold ? 2 : 0) + (!gate ? 2 : 0) +
                     (fixOff ? 2 : 0) + (heldAt < 0 ? 1 : 0) + (swap ? 1 : 0) + (hits.Length > 0 ? 1 : 0);
        c.Line = $"{DiagTag}e s={_diagClock!.Samples} t={_diagClock.Elapsed(Environment.TickCount64)} u={c.Uuid} k={c.Kind} cls={r.EntClass} " +
                 $"id={r.EntId} boss={(r.Boss ? 1 : 0)} d={c.Dist:F1} in={c.Colls} tgt={tgt} st1={YN(st1)} st2={YN(st2)} held={YN(heldAt >= 0)} holdLive={YN(holdLive)} " +
                 $"gate={YN(gate)} | spd={F(r.Drawn)} ctl={F(r.ControllerSpeed)} ctlCls={r.ControllerClass} compCls={r.CompClass} " +
                 $"fac={F(r.Factor)} asp={F(r.AnimSpeed)} skill={r.Skill} skFx={r.SkillEffect} stage={stage} act={r.Action} st={r.State} | " +
                 $"logic={V(r.Logic)} drawn={V(r.DrawnPos)} dHold={F(dHold)} dLogic={F(Dist(r.DrawnPos, r.Logic))} " +
                 $"swap={row.SwapText(r.Model, r.Comp, r.Controller, r.Go)} | fix: {fix} | n:{(hits.Length == 0 ? " -" : hits)}";
        DiagSampleCounts((drawnAnim ? 1 : 0) | (ctlAnim ? 2 : 0) | (offHold ? 4 : 0) | (tgt == "none" ? 8 : 0) | (heldAt < 0 ? 16 : 0) | (!gate ? 32 : 0));
    }

    /// <summary>" sub=3 ctlUp=2 …" — this entity's non-zero hit counts since its previous sample.</summary>
    private static string DiagHitsText(int[] n)
    {
        var s = "";
        for (var i = 0; i < n.Length; i++)
            if (n[i] != 0) s += $" {SlotName((DiagSlot)i)}={n[i]}";
        return s;
    }

    internal static string SlotName(DiagSlot slot) => slot switch
    {
        DiagSlot.GateSub => "sub", DiagSlot.GatePassUntracked => "passU", DiagSlot.GatePassExcluded => "passX",
        DiagSlot.GatePassOwn => "passOwn", DiagSlot.CtlSpeed => "ctlSet", DiagSlot.CtlSpeedUp => "ctlUp",
        DiagSlot.CtlPause => "ctlPause", DiagSlot.AnimPlay => "play", DiagSlot.CtlPlay => "ctlPlay",
        DiagSlot.SkillStage => "stage", DiagSlot.SkillTick => "skTick", DiagSlot.SkillStep => "skStep",
        DiagSlot.MoveTick => "mvTick", DiagSlot.MoveGo => "moveGo", DiagSlot.GoPosBefore => "posPre",
        DiagSlot.GoPosAfter => "posPost", DiagSlot.FxDisplay => "fxDisp", DiagSlot.FxBinder => "fxBind",
        DiagSlot.FxInit => "fxInit", DiagSlot.FxAdd => "fxAdd", DiagSlot.FxUnfreeze => "fxUnfrz", DiagSlot.FxSpeed => "fxSpd",
        _ => slot.ToString(),
    };

    private static Vector3? CameraPos()
    {
        try { return Camera.main is { } c ? c.transform.position : null; }
        catch { return null; }
    }

    private static float Dist(Vector3? a, Vector3? b) => a is { } x && b is { } y ? Vector3.Distance(x, y) : float.NaN;
    private static string YN(bool b) => b ? "y" : "n";
    private static string F(float v) => float.IsNaN(v) ? "?" : v.ToString("F2", CultureInfo.InvariantCulture);
    private static string V(Vector3? v) => v is { } p ? string.Create(CultureInfo.InvariantCulture, $"({p.x:F1},{p.y:F1},{p.z:F1})") : "?";
}
