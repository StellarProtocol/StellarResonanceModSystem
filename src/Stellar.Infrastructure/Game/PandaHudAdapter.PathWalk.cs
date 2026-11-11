using System.Collections.Generic;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Path resolution half of <see cref="PandaHudAdapter"/>: every allowlist walk (element path, RectChild tokens,
/// SeenGate node, the resolve-time curate diagnostics) goes through <see cref="WrapperPath"/> so it sees THROUGH our
/// own <c>stellar_wrap_*</c> nodes — nested elements (class gauge inside Player HP/MP) resolve whichever was wrapped
/// first (Native-UI-Layout-Positioning.md, "Nested elements").
/// </summary>
internal sealed partial class PandaHudAdapter
{
    // Every wrapper we created and haven't discarded. Static because the tree ops are used from the static measure
    // helpers; there is one adapter per process. Feeds the alloc-free MayHoldWrapper pre-check: a missing segment
    // (party panel's "main_team_sub_pc(Clone)" outside a party, measured every pass) costs ≤15 IsChildOf value calls
    // and NO child scan / name read unless one of our wrappers really sits below that node.
    private static readonly List<RectTransform> LiveWrappers = new();
    private static readonly Dictionary<string, string[]> SubPathSegments = new();

    private static void RegisterWrapper(RectTransform w) => LiveWrappers.Add(w);

    private static void UnregisterWrapper(RectTransform w)
    {
        for (var i = LiveWrappers.Count - 1; i >= 0; i--)
            if (LiveWrappers[i] == null || LiveWrappers[i].Pointer == w.Pointer) LiveWrappers.RemoveAt(i);
    }

    // Walk the FULL allowlist path one segment at a time from the cached root. Transform.Find also returns INACTIVE
    // children (GameObject.Find only sees active objects), so a solo player's hidden party panel still resolves —
    // which is what the removed every-5-s Resources.FindObjectsOfTypeAll<Canvas>() fallback used to be needed for.
    // Exact path only, never a name search: photo mode builds a SECOND FighterBtnsView copy elsewhere. Il2Cpp's
    // Transform.Find can't take a multi-segment path, hence the per-segment walk (segments split once per path).
    private Transform? WalkPath(string path)
    {
        if (!_pathSegments.TryGetValue(path, out var segs)) _pathSegments[path] = segs = path.Split('/');
        if (_root == null)
        {
            var go = GameObject.Find(segs[0]);
            if (go == null) return null;
            _root = go.transform;
        }
        return WrapperPath.Walk(default(UnityTree), _root, segs, 1);
    }

    // Resolve a '/'-separated child path one segment at a time (Il2Cpp's Transform.Find returns null for a
    // multi-segment path). "." / empty segments = the node itself. Segments are split once per distinct sub-path —
    // this runs per RectChild token on every Measure, so no per-call Split.
    private static Transform? FindByPath(Transform root, string path)
    {
        if (!SubPathSegments.TryGetValue(path, out var segs)) SubPathSegments[path] = segs = path.Split('/');
        return WrapperPath.Walk(default(UnityTree), root, segs);
    }

    private static Transform? FindChildThroughWrappers(Transform node, string name)
        => WrapperPath.FindChild(default(UnityTree), node, name);

    // Unity side of IWrapTree. Find normalizes Unity's "fake null" to a real null so WrapperPath's reference checks hold.
    // IsWrapper reads .name (an Il2Cpp string) — only reached after MayHoldWrapper said one of ours is below.
    private readonly struct UnityTree : IWrapTree<Transform>
    {
        public Transform? Find(Transform node, string name)
        {
            var r = node.Find(name);
            return r == null ? null : r;
        }

        public bool MayHoldWrapper(Transform node)
        {
            for (var i = LiveWrappers.Count - 1; i >= 0; i--)
            {
                var w = LiveWrappers[i];
                if (w == null) { LiveWrappers.RemoveAt(i); continue; }   // died with a rebuilt prefab
                if (w.IsChildOf(node)) return true;
            }
            return false;
        }

        public int ChildCount(Transform node) => node.childCount;
        public Transform? Child(Transform node, int index) => node.GetChild(index);
        public bool IsWrapper(Transform node) => WrapperPath.IsWrapperName(node.name);
    }
}
