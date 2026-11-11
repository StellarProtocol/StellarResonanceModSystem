using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Stellar.Abstractions.Diagnostics;

// Per-HANDLER hook attribution for the [Hitch] line. The combatHooks / wireHooks totals say THAT hook time ate a
// frame (raid 2026-11-09: 424 ms / 202 ms in single frames) but not WHOSE handler; this names the top three per
// kind, e.g. `combatTop=PandaCombatStubProbe#26:180.2ms/12,PandaDungeonProbe#40:1.1ms/2`.
//
// Cost model: a dispatcher asks for a slot ONCE, at handler registration, and only when hitch attribution is
// armed — otherwise it keeps the bare handler (no wrapper, zero per-call cost). Armed, each call is two
// Stopwatch reads + two Interlocked adds on a pre-allocated slot (no per-call allocation; hook threads are
// fine). Slots are deduped by (kind, name), so a subscriber that re-subscribes doesn't grow the registry.
public static partial class HitchProbe
{
    /// <summary>Attribution kind: game-network combat stub handlers (summed in <c>combatHooks</c>).</summary>
    public const int HookKindCombat = 0;
    /// <summary>Attribution kind: wire tap / notify stub handlers (summed in <c>wireHooks</c>).</summary>
    public const int HookKindWire = 1;
    /// <summary>Attribution kind: <c>ICombatEvents.CombatEventOccurred</c> subscribers (main-thread fan-out,
    /// inside the <c>svc:combat</c> segment).</summary>
    public const int HookKindCombatSubscriber = 2;
    private const int HookKinds = 3;

    /// <summary>A pre-allocated per-handler accumulator. Opaque to callers — obtain via <see cref="HookSlotFor"/>.</summary>
    public sealed class HookSlot
    {
        internal readonly int Kind;
        internal readonly string Name;
        internal long Ticks, Calls;
        internal HookSlot(int kind, string name) { Kind = kind; Name = name; }
    }

    // Copy-on-write so the per-frame reader never locks; registration (rare, any thread) takes _slotLock.
    private static HookSlot[] _slots = Array.Empty<HookSlot>();
    private static readonly Dictionary<(int, string), HookSlot> _slotByKey = new();
    private static readonly object _slotLock = new();

    /// <summary>The slot for handler <paramref name="name"/> of <paramref name="kind"/>, or <c>null</c> when hitch
    /// attribution is off (then keep the bare handler). Call at registration time, never per call.</summary>
    public static HookSlot? HookSlotFor(int kind, string name)
    {
        if (!_enabled || (uint)kind >= HookKinds) return null;
        lock (_slotLock)
        {
            if (_slotByKey.TryGetValue((kind, name), out var s)) return s;
            s = new HookSlot(kind, name);
            _slotByKey[(kind, name)] = s;
            var next = new HookSlot[_slots.Length + 1];
            Array.Copy(_slots, next, _slots.Length);
            next[_slots.Length] = s;
            Volatile.Write(ref _slots, next);
            return s;
        }
    }

    /// <summary>Charge one handler invocation started at <paramref name="startTicks"/> (<see cref="Begin"/>) to
    /// <paramref name="slot"/>. Any thread.</summary>
    public static void EndHookSlot(HookSlot? slot, long startTicks)
    {
        if (slot is null || startTicks == 0L) return;
        Interlocked.Add(ref slot.Ticks, Stopwatch.GetTimestamp() - startTicks);
        Interlocked.Increment(ref slot.Calls);
    }

    /// <summary>A stable, readable owner name for a handler delegate: its declaring type (closure / lambda
    /// classes folded into the type that wrote them), prefixed by the assembly when that differs and isn't a
    /// framework assembly — e.g. <c>RaidManager.MechanicTracker</c>, <c>PandaCombatStubProbe</c>. Registration
    /// time only (reflection).</summary>
    public static string DescribeHandler(Delegate handler)
    {
        try
        {
            var type = handler.Method.DeclaringType;
            while (type is { DeclaringType: not null } && type.Name.StartsWith("<", StringComparison.Ordinal))
                type = type.DeclaringType;
            if (type is null) return handler.Method.Name;
            var asm = type.Assembly.GetName().Name ?? "";
            if (asm.StartsWith("Stellar.", StringComparison.Ordinal))
            {
                var tail = asm.Substring("Stellar.".Length);
                // Framework layers (Infrastructure/Application/Host/…) add noise; plugin assemblies identify the plugin.
                if (tail is "Infrastructure" or "Application" or "Host" or "Wire" or "Abstractions") return type.Name;
                asm = tail;
            }
            return asm.Length == 0 || asm == type.Name ? type.Name : asm + "." + type.Name;
        }
        catch { return "?"; }
    }

    // Top-3 scratch (main thread, Format only) — no allocation on the hitch path beyond the line itself.
    private static readonly HookSlot?[] _top = new HookSlot?[3];
    private static readonly long[] _topTicks = new long[3];
    private static readonly long[] _topCalls = new long[3];

    private static void AppendHookTop(StringBuilder sb, string label, int kind)
    {
        var slots = Volatile.Read(ref _slots);
        for (var i = 0; i < 3; i++) { _top[i] = null; _topTicks[i] = 0; _topCalls[i] = 0; }
        foreach (var s in slots)
        {
            if (s.Kind != kind) continue;
            var calls = Interlocked.Read(ref s.Calls);
            if (calls == 0) continue;
            var ticks = Interlocked.Read(ref s.Ticks);
            for (var i = 0; i < 3; i++)
            {
                if (_top[i] != null && ticks <= _topTicks[i]) continue;
                for (var j = 2; j > i; j--) { _top[j] = _top[j - 1]; _topTicks[j] = _topTicks[j - 1]; _topCalls[j] = _topCalls[j - 1]; }
                _top[i] = s; _topTicks[i] = ticks; _topCalls[i] = calls;
                break;
            }
        }
        if (_top[0] is null) return;
        sb.Append(label);
        for (var i = 0; i < 3 && _top[i] is { } t; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(t.Name).Append(':').Append((_topTicks[i] * 1000.0 / Stopwatch.Frequency).ToString("0.0"))
              .Append("ms/").Append(_topCalls[i]);
        }
    }

    private static void ResetHookSlots()
    {
        foreach (var s in Volatile.Read(ref _slots)) { Interlocked.Exchange(ref s.Ticks, 0L); Interlocked.Exchange(ref s.Calls, 0L); }
    }
}
