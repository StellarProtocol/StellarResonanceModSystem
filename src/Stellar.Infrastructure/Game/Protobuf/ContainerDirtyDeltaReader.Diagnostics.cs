using System;
using System.Text;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Opt-in discovery diagnostics for <see cref="ContainerDirtyDeltaReader"/>.
/// All emission is gated behind <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state
/// parse path pays at most a single static-field read per delta.
///
/// <para>Goal: a single manual in-game equip must yield enough structure for a
/// human to confirm (or correct) the scalar-valued-map assumption that BPSR-B
/// does not exercise. Every line is prefixed <c>[Inventory][Delta]</c> so it
/// greps cleanly. On any parse failure we dump the offset plus a few
/// surrounding little-endian i32 values instead of throwing.</para>
/// </summary>
internal static partial class ContainerDirtyDeltaReader
{
    private const string Tag = "[Inventory][Delta]";
    private static Action<string>? _diagnosticLog;

    /// <summary>
    /// Registers the log sink used for discovery dumps. Called once at
    /// composition time (Host) so the infrastructure layer keeps its hold on
    /// <c>IPluginLog</c>. No-op when <c>STELLAR_DIAGNOSTICS</c> is off.
    /// </summary>
    public static void SetDiagnosticLog(Action<string> log)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _diagnosticLog = log ?? throw new ArgumentNullException(nameof(log));
    }

    private static void Emit(string message)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var log = _diagnosticLog;
        if (log is null) return;
        try { log($"{Tag} {message}"); } catch { /* never let diagnostic logging escape */ }
    }

    private static void DiagBufferLength(int length)
        => Emit($"delta received: buffer length={length}");

    private static void DiagTopField(int index, int offset)
        => Emit($"CharSerialize field index={index} @offset={offset}");

    private static void DiagModField(int index, int offset)
        => Emit($"Mod (field 57) inner field index={index} @offset={offset}");

    private static void DiagModSlotsCounts(int add, int remove, int update, bool skip)
        => Emit(skip
            ? "mod_slots (field 1): MAP_SKIP — no change"
            : $"mod_slots (field 1): addCount={add} removeCount={remove} updateCount={update}");

    private static void DiagModSlotEntry(string kind, int slot, long uuid)
        => Emit($"mod_slots {kind}: slot={slot} uuid={uuid}");

    private static void DiagModSlotRemove(int slot)
        => Emit($"mod_slots remove: slot={slot}");

    private static void DiagBadBeginTag(string label, int tag)
        => Emit($"{label}: expected BEGIN({TagBegin}) but read {tag}");

    // On a parse failure, surface the offset and the next few raw i32 values so a
    // human can see the actual encoding around the break (e.g. if the scalar-map
    // assumption is wrong, the slot/uuid words it tried to read are visible here).
    private static void DiagParseFailure(string where, ref BlobReader reader)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var around = reader.PeekInt32s(6);
        var sb = new StringBuilder();
        sb.Append("parse failure at ").Append(where)
          .Append(": offset=").Append(reader.Offset)
          .Append(" remaining=").Append(reader.Remaining)
          .Append(" nextI32=[");
        for (var i = 0; i < around.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(around[i]);
        }
        sb.Append(']');
        Emit(sb.ToString());
    }

    // Emits the final equipped-set size after a successful delta apply. Called
    // from the probe boundary (PandaInventoryProbe.DirtyDelta.cs) since the
    // reader itself is snapshot-agnostic.
    internal static void DiagEquippedSetSize(int size)
        => Emit($"equipped-set size after apply={size}");
}
