using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Time-sliced table loads for <see cref="PandaGameDataProbe"/> (2026-11-10).
///
/// <para><b>Why:</b> every table used to load in ONE call — the whole eager batch (8 tables) in a single
/// frame, then one whole deferred table per tick. Raid log 2026-11-10: <c>fw:gamedata</c> up to 275 ms at
/// login (eager batch 267 ms; Buff 124 ms, Item 61 ms) and single deferred tables of 252 ms (DamageAttr),
/// 180 ms (EquipRow), 120 ms (EquipSchoolAttrLib). The cost is per ROW (IL2CPP enumerator + reflective
/// column reads — the Bokura rows are expensive to touch), so splitting by table alone could not help the
/// big ones. A <see cref="TableLoadJob"/> walks rows until a deadline and resumes on the next tick; the
/// result (and any per-loader post-processing) is produced only once the walk is complete, so consumers
/// still see each table appear atomically.</para>
///
/// <para><b>Cross-tick enumerator safety:</b> the typed <c>ZTable</c> enumerator is held across ticks.
/// Config tables are static after load, but should the game's dictionary ever change between ticks (its
/// enumerator then throws on <c>MoveNext</c>), the cursor re-opens the enumerator ONCE and finishes that
/// table in a single unsliced pass — the old behaviour, so a table is never silently truncated.</para>
/// </summary>
internal sealed partial class PandaGameDataProbe
{
    /// <summary>One resumable table load. <see cref="Step"/> walks rows until the deadline (a
    /// <see cref="Stopwatch"/> timestamp; <see cref="long.MaxValue"/> = run to the end) and returns true once
    /// the table is finished, at which point <see cref="Result"/> holds the loader's value.</summary>
    private abstract class TableLoadJob
    {
        public abstract bool Step(long deadline);
        public abstract object? Result { get; }

        public object? RunToEnd()
        {
            while (!Step(long.MaxValue)) { }
            return Result;
        }
    }

    private sealed class TableLoadJob<TInfo> : TableLoadJob where TInfo : struct
    {
        private readonly PandaGameDataProbe _probe;
        private readonly TableSpec _spec;
        private readonly Func<object, Type, (int id, TInfo info)> _projector;
        private readonly Func<IReadOnlyDictionary<int, TInfo>, object>? _finish;
        private readonly Dictionary<int, TInfo> _rows;
        private RowCursor? _cursor;
        private bool _started, _done;
        private object? _result;
        private long _activeTicks;
        private int _slices;

        public TableLoadJob(PandaGameDataProbe probe, TableSpec spec,
            Func<object, Type, (int id, TInfo info)> projector, Func<IReadOnlyDictionary<int, TInfo>, object>? finish)
        {
            _probe = probe; _spec = spec;
            _projector = projector; _finish = finish;
            _rows = new Dictionary<int, TInfo>(capacity: spec.CapacityHint);
        }

        public override object? Result => _result;

        public override bool Step(long deadline)
        {
            if (_done) return true;
            var started = Stopwatch.GetTimestamp();
            _slices++;
            var finished = true;
            // Same failure contract as the old one-shot envelope: a throw logs once and keeps the rows so far.
            try { finished = Advance(deadline); }
            catch (Exception ex) { _probe._log.Error($"[Stellar][GameData] Load{_spec.Label} threw: {ex.GetType().Name}: {ex.Message}"); }
            _activeTicks += Stopwatch.GetTimestamp() - started;
            if (!finished)
            {
                _cursor!.Suspended = true;
                return false;
            }
            Complete();
            return true;
        }

        private bool Advance(long deadline)
        {
            if (!_started)
            {
                _started = true;
                _cursor = _probe.OpenRowCursor(_spec.TypeName, _spec.Label);
                if (_cursor is null) return true;
            }
            while (_cursor!.TryNext(out var row))
            {
                if (row is not null)
                {
                    var (id, info) = _projector(row, row.GetType());
                    if (id != 0) _rows[id] = info;
                }
                // At least one row per step, so a load always progresses. MustFinish = the enumerator was
                // invalidated across a tick and re-opened: finish this pass unsliced (see the type summary).
                if (!_cursor.MustFinish && Stopwatch.GetTimestamp() >= deadline) return false;
            }
            return true;
        }

        private void Complete()
        {
            _done = true;
            _cursor?.Close();
            var ms = _activeTicks * 1000L / Stopwatch.Frequency;
            var slices = _slices > 1 ? $", {_slices} slices" : string.Empty;
            _probe._log.Info($"[Stellar][GameData] {_spec.Batch}: {_spec.Label} loaded ({_rows.Count} rows, {ms}ms{slices})");
            _result = _finish is null ? _rows : _finish(_rows);
        }
    }

    // Which table a job loads + how it logs ("eager"/"deferred" batch label, display label, Bokura type).
    private readonly record struct TableSpec(string Batch, string Label, string TypeName, int CapacityHint);

    private TableLoadJob EagerJob<TInfo>(string label, string typeName, int capacityHint,
        Func<object, Type, (int id, TInfo info)> projector, Func<IReadOnlyDictionary<int, TInfo>, object>? finish = null)
        where TInfo : struct
        => new TableLoadJob<TInfo>(this, new TableSpec("eager", label, typeName, capacityHint), projector, finish);

    private TableLoadJob DeferredJob<TInfo>(string label, string typeName, int capacityHint,
        Func<object, Type, (int id, TInfo info)> projector, Func<IReadOnlyDictionary<int, TInfo>, object>? finish = null)
        where TInfo : struct
        => new TableLoadJob<TInfo>(this, new TableSpec("deferred", label, typeName, capacityHint), projector, finish);

    // Resolve the table type + ZTable and open a row cursor on it; null (after logging) when either is missing.
    private RowCursor? OpenRowCursor(string typeName, string label)
    {
        var tableType = _typeRegistry.FindType(typeName);
        if (tableType is null)
        {
            _log.Warning($"[Stellar][GameData] missing type {typeName}");
            return null;
        }
        if (!TryGetTable(tableType, out var table) || table is null) return null;
        LogTableShape(label + "TableBase", table);
        return new RowCursor(this, table);
    }

    /// <summary>Resumable form of <see cref="EnumerateRowsAsObjects"/>: the same typed-enumerator walk, the
    /// same per-row read rules (a throwing <c>Current</c> skips the row, a throwing <c>Value</c> yields null)
    /// and the same dense-key fallback when the enumerator yields nothing on a non-empty table — one row
    /// per <see cref="TryNext"/>, so a load can stop between rows and continue next tick.</summary>
    private sealed class RowCursor
    {
        private readonly PandaGameDataProbe _probe;
        private readonly object _table;
        private object? _enumerator;
        private MethodInfo? _moveNext;
        private PropertyInfo? _current, _kvpValue;
        private List<object?>? _fallback;
        private int _fallbackPos, _yielded, _iterations;
        private bool _fallbackTried, _restarted;

        /// <summary>Set by the job when it paused between rows (the enumerator now spans ticks).</summary>
        public bool Suspended { get; set; }

        /// <summary>True once the enumerator was re-opened after a cross-tick invalidation.</summary>
        public bool MustFinish => _restarted;

        public RowCursor(PandaGameDataProbe probe, object table)
        {
            _probe = probe;
            _table = table;
            Open();
        }

        private void Open()
        {
            _enumerator = null;
            var tableType = _table.GetType();
            var getEnumerator = FindTypedGetEnumerator(tableType);
            if (getEnumerator is null) return;
            if (!_probe.TryInvokeGetEnumerator(getEnumerator, _table, tableType.Name, out var enumerator) || enumerator is null) return;
            var enumeratorType = enumerator.GetType();
            _moveNext = enumeratorType.GetMethod("MoveNext", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null);
            _current = enumeratorType.GetProperty("Current", AnyInstance);
            if (_moveNext is null || _current is null) return;
            _enumerator = enumerator;
        }

        public bool TryNext(out object? row)
        {
            row = null;
            if (_fallback is not null)
            {
                if (_fallbackPos >= _fallback.Count) return false;
                row = _fallback[_fallbackPos++];
                return true;
            }
            const int safetyCap = 100_000;   // same runaway-proxy guard as DrainEnumeratorRows
            while (_enumerator is not null && _iterations++ < safetyCap)
            {
                bool advanced;
                try { advanced = (bool)(_moveNext!.Invoke(_enumerator, Array.Empty<object>()) ?? false); }
                catch
                {
                    if (TryRestart()) continue;
                    advanced = false;
                }
                if (!advanced) break;
                if (TryReadCurrent(out row)) { _yielded++; return true; }
            }
            Close();
            return EnterFallbackIfEmpty(out row);
        }

        // Mirrors AppendCurrentRow: false = skip this row (Current threw); true = a row (possibly null).
        private bool TryReadCurrent(out object? row)
        {
            row = null;
            object? current;
            try { current = _current!.GetValue(_enumerator); }
            catch { return false; }
            if (current is null) return true;
            _kvpValue ??= current.GetType().GetProperty("Value", AnyInstance);
            if (_kvpValue is null) { row = current; return true; }
            try { row = _kvpValue.GetValue(current); }
            catch { row = null; }
            return true;
        }

        // A MoveNext that throws AFTER the walk was suspended across a tick means the game's dictionary
        // changed under us: re-open once and finish the table in one pass (rows are keyed by id, so the
        // re-walked rows simply overwrite). A throw without suspension keeps the old "stop here" behaviour.
        private bool TryRestart()
        {
            if (!Suspended || _restarted) return false;
            _restarted = true;
            _probe._log.Warning("[Stellar][GameData] table enumerator invalidated between slices; re-walking it in one pass");
            Close();
            _yielded = 0;
            _iterations = 0;
            Open();
            return _enumerator is not null;
        }

        private bool EnterFallbackIfEmpty(out object? row)
        {
            row = null;
            if (_yielded > 0 || _fallbackTried) return false;
            _fallbackTried = true;
            if (!TryGetReportedCount(_table, out var reportedCount) || reportedCount <= 0) return false;
            _probe._log.Warning(
                $"[Stellar][GameData] typed-enumerator yielded 0 rows on {_table.GetType().Name} (Count={reportedCount}); " +
                "falling back to dense-key TryGetValue scan");
            _fallback = _probe.CollectRowsViaDenseKeyScan(_table, reportedCount);
            return TryNext(out row);
        }

        public void Close()
        {
            if (_enumerator is null) return;
            TryDispose(_enumerator);
            _enumerator = null;
        }
    }
}
