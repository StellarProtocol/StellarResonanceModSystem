using UnityEngine;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Last-seen GATE of an allowlist element (<c>NativeUiAllowlistEntry.SeenGate</c>): a measurement is a rest-state
/// sample only while the gate node resolves and its <c>localScale.x</c> ≤ the max. The Quickbar's [L Ctrl] skill wheel
/// (<c>battle_main_node_window_skill_drawer_expand_pc</c>) rests at scale 0.52 and is 1.00 while Ctrl holds it expanded
/// — in town the only time the bar shows at all, so without the gate the expanded size became the placeholder.
/// </summary>
internal sealed partial class PandaHudAdapter
{
    private static ResolvedSeenGate? NewSeenGate(NativeUiSeenGate? g)
        => g == null ? null : new ResolvedSeenGate { Segments = g.Path.Split('/'), MaxScale = g.MaxScale };

    // Alloc-free on a hit: the gate Transform is cached and only localScale (a struct) is read. A miss (not built yet,
    // or destroyed — Unity's == null) re-finds it from the outline root, allocating Il2Cpp wrappers only then; a fresh
    // TryResolve (scene rebuild) builds a new ResolvedEntry, so a stale node never survives a rebuild.
    private static bool SeenGateOpen(ResolvedEntry e)
    {
        var g = e.SeenGate;
        if (g == null) return true;
        var t = g.Node;
        if (t == null)
        {
            t = WrapperPath.Walk(default(UnityTree), (Transform)OutlineRoot(e), g.Segments);   // through our wrappers
            if (t == null) return false;   // can't tell the state → not a rest sample
            g.Node = t;
        }
        return t.localScale.x <= g.MaxScale;
    }

    private sealed class ResolvedSeenGate
    {
        public string[] Segments = System.Array.Empty<string>();
        public float MaxScale;
        public Transform? Node;
    }
}
