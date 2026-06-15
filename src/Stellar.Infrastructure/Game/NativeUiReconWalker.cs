using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Static helper that walks every loaded UnityEngine.Canvas and emits one
/// log line per Transform with hierarchy path + active state. Used by both
/// the Settings → Game UI panel's recon button and the boot-time auto-recon
/// gated on STELLAR_NATIVEUI_RECON=1.
/// </summary>
/// <remarks>
/// Bounded against runaway hierarchies on three axes: depth is capped at
/// <see cref="MaxDepth"/>; the breadth visited per node is capped at
/// <see cref="MaxChildrenPerNode"/> (so template explosions like the minimap's
/// hundreds of GUID-named map flags or the 100+ skill-slot buttons can't
/// starve the budget before the walk reaches other canvases); and the total
/// number of emitted lines is capped at <see cref="MaxEntries"/> (overridable
/// via <c>STELLAR_RECON_LIMIT</c>). A start/done log frame brackets the dump
/// so the caller sees feedback even when a cap fires.
/// Paths are built with a <see cref="StringBuilder"/> via a parent stack
/// — previous impl allocated a new <see cref="List{T}"/> + did <c>Insert(0,)</c>
/// per transform.
/// </remarks>
internal static class NativeUiReconWalker
{
    private const int MaxDepth = 8;
    private const int MaxChildrenPerNode = 12;
    private const int DefaultMaxEntries = 2000;

    public static void Walk(Action<string> logInfo)
    {
        var ctx = new WalkContext(logInfo, ReadMaxEntriesEnv());
        logInfo($"[NativeUi/Recon] walk starting (max depth={MaxDepth}, max entries={ctx.MaxEntries})");

        var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
        var canvasesVisited = 0;
        foreach (var canvas in canvases)
        {
            if (canvas == null || canvas.transform == null) continue;
            canvasesVisited++;
            WalkTransform(canvas.transform, depth: 0, ctx);
            if (ctx.Truncated) break;
        }

        if (ctx.Truncated)
            logInfo($"[NativeUi/Recon] … (truncated at {ctx.Emitted}; raise STELLAR_RECON_LIMIT to see more)");
        logInfo($"[NativeUi/Recon] walk done (visited {canvasesVisited} canvases, {ctx.Emitted} nodes)");
    }

    private static int ReadMaxEntriesEnv()
    {
        var raw = Environment.GetEnvironmentVariable("STELLAR_RECON_LIMIT");
        if (string.IsNullOrEmpty(raw)) return DefaultMaxEntries;
        return int.TryParse(raw, out var n) && n > 0 ? n : DefaultMaxEntries;
    }

    private static void WalkTransform(Transform t, int depth, WalkContext ctx)
    {
        if (t == null || ctx.Truncated) return;
        if (ctx.Emitted >= ctx.MaxEntries) { ctx.Truncated = true; return; }
        if (depth > MaxDepth) return;

        EmitLine(t, depth, ctx);
        ctx.Emitted++;

        var childCount = t.childCount;
        var toVisit = childCount < MaxChildrenPerNode ? childCount : MaxChildrenPerNode;
        for (var i = 0; i < toVisit; i++)
        {
            WalkTransform(t.GetChild(i), depth + 1, ctx);
            if (ctx.Truncated) return;
        }
        if (childCount > toVisit) EmitMore(t, childCount - toVisit, depth + 1, ctx);
    }

    private static void EmitMore(Transform parent, int hidden, int depth, WalkContext ctx)
    {
        if (ctx.Emitted >= ctx.MaxEntries) { ctx.Truncated = true; return; }
        ctx.LogInfo($"[NativeUi/Recon] {new string(' ', depth * 2)}… (+{hidden} more children of {parent.name}; raise MaxChildrenPerNode to see)");
        ctx.Emitted++;
    }

    private static void EmitLine(Transform t, int depth, WalkContext ctx)
    {
        BuildHierarchyPath(t, ctx.PathSb, ctx.AncestorStack);
        // Il2CppInterop: the wrapper returned by GetChild() is statically typed
        // Transform, so a plain `as RectTransform` always yields null even for
        // UGUI nodes. TryCast does the real Il2CPP type check.
        var rt = t.TryCast<RectTransform>();
        var rectInfo = rt != null
            ? $"({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0} {rt.sizeDelta.x:0}x{rt.sizeDelta.y:0})"
            : string.Empty;
        ctx.LogInfo($"[NativeUi/Recon] {new string(' ', depth * 2)}{ctx.PathSb} active={t.gameObject.activeSelf} {rectInfo}");
    }

    private static void BuildHierarchyPath(Transform t, StringBuilder sb, Stack<string> stack)
    {
        stack.Clear();
        sb.Clear();
        var cur = t;
        while (cur != null) { stack.Push(cur.name); cur = cur.parent; }
        var first = true;
        while (stack.Count > 0)
        {
            if (!first) sb.Append('/');
            sb.Append(stack.Pop());
            first = false;
        }
    }

    /// <summary>
    /// Parameter object for the recursive walk. Bundles the log sink, the
    /// max-entries cap, the running emit counter + truncation flag, and the
    /// reusable scratch buffers — so the recursive call site stays under the
    /// STELLAR0003 5-param gate.
    /// </summary>
    private sealed class WalkContext
    {
        public WalkContext(Action<string> logInfo, int maxEntries)
        {
            LogInfo = logInfo;
            MaxEntries = maxEntries;
        }
        public Action<string> LogInfo     { get; }
        public int            MaxEntries  { get; }
        public int            Emitted     { get; set; }
        public bool           Truncated   { get; set; }
        public StringBuilder  PathSb        { get; } = new(256);
        public Stack<string>  AncestorStack { get; } = new(16);
    }
}
