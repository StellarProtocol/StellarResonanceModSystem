using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Wrapper-transparent path resolution (2026-11-11 class-gauge bug): the class gauge's allowlist path runs
/// THROUGH Player HP/MP's node, so once Player HP/MP was outer-wrapped the plain Transform.Find walk broke at our
/// <c>stellar_wrap_gameui.player-hp</c> node and the gauge never resolved. Pure walk over a fake tree.</summary>
public sealed class WrapperPathTests
{
    private sealed class Node
    {
        public readonly string Name;
        public readonly List<Node> Kids = new();
        public Node(string name, params Node[] kids) { Name = name; Kids.AddRange(kids); }
    }

    private struct Tree : IWrapTree<Node>
    {
        public bool NoWrappers;   // what the Unity pre-check returns when none of OUR wrappers sits below the node

        public Node? Find(Node node, string name)
        {
            foreach (var k in node.Kids) if (k.Name == name) return k;
            return null;
        }
        public bool MayHoldWrapper(Node node) => !NoWrappers;
        public int ChildCount(Node node) => node.Kids.Count;
        public Node? Child(Node node, int index) => node.Kids[index];
        public bool IsWrapper(Node node) => WrapperPath.IsWrapperName(node.Name);
    }

    private static readonly string[] GaugePath =
        "zuiroot/anim_battle/node_player_state_bar_hide_root/node_player_state_bar/group_info_layout/group_energy".Split('/');

    private static Node Gauge() => new("group_energy", new Node("battle_res_uibinder_11"));

    private static Node StateBar(Node gauge)
        => new("node_player_state_bar_hide_root",
            new Node("node_player_state_bar", new Node("group_info_layout", new Node("group_blood"), gauge)));

    [Fact] // Unwrapped hierarchy: plain walk, unchanged behaviour.
    public void Plain_Resolves()
    {
        var gauge = Gauge();
        var root = new Node("zuiroot", new Node("anim_battle", StateBar(gauge)));
        Assert.Same(gauge, WrapperPath.Walk(new Tree(), root, GaugePath, 1));
    }

    [Fact] // The live bug: Player HP/MP (outer) wrapped first — the gauge still resolves through the wrapper.
    public void OuterWrapperOfAncestor_IsTransparent()
    {
        var gauge = Gauge();
        var root = new Node("zuiroot", new Node("anim_battle",
            new Node("node_protection"), new Node("stellar_wrap_gameui.player-hp", StateBar(gauge))));
        Assert.Same(gauge, WrapperPath.Walk(new Tree(), root, GaugePath, 1));
    }

    [Fact] // Reverse order (gauge's INNER wrapper exists, then HP/MP outer-wraps the ancestor) and stacked wrappers.
    public void NestedWrappers_AreTransparent()
    {
        var gauge = new Node("group_energy", new Node("stellar_wrap_gameui.class-gauge", new Node("battle_res_uibinder_11")));
        var root = new Node("zuiroot", new Node("anim_battle",
            new Node("stellar_wrap_a", new Node("stellar_wrap_b", StateBar(gauge)))));
        Assert.Same(gauge, WrapperPath.Walk(new Tree(), root, GaugePath, 1));
        // A sub-path below the gauge (RectChild / SeenGate style) also crosses the gauge's own inner wrapper.
        Assert.Equal("battle_res_uibinder_11",
            WrapperPath.Walk(new Tree(), gauge, new[] { "battle_res_uibinder_11" })?.Name);
    }

    [Fact] // A direct hit wins over a same-named node inside a wrapper; game nodes are never looked through.
    public void DirectHitWins_AndGameNodesAreNotTransparent()
    {
        var direct = new Node("x");
        var root = new Node("r", new Node("stellar_wrap_q", new Node("x")), direct, new Node("other", new Node("y")));
        Assert.Same(direct, WrapperPath.FindChild(new Tree(), root, "x"));
        Assert.Null(WrapperPath.FindChild(new Tree(), root, "y"));
    }

    [Fact] // "." and empty segments = the node itself ("*." RectChild tokens); a missing segment → null.
    public void DotSegments_AndMisses()
    {
        var root = new Node("r", new Node("a", new Node("b")));
        Assert.Same(root, WrapperPath.Walk(new Tree(), root, new[] { "." }));
        Assert.Equal("b", WrapperPath.Walk(new Tree(), root, new[] { "a", "", "b" })?.Name);
        Assert.Null(WrapperPath.Walk(new Tree(), root, new[] { "a", "zz", "b" }));
    }

    [Fact] // Pre-check says no wrapper below → no wrapper scan, even if a wrapper-named child exists (alloc-free miss).
    public void PreCheckFalse_SkipsScan()
    {
        var root = new Node("r", new Node("stellar_wrap_q", new Node("x")));
        Assert.Null(WrapperPath.FindChild(new Tree { NoWrappers = true }, root, "x"));
    }

    [Fact]
    public void WrapperName_Prefix()
    {
        Assert.True(WrapperPath.IsWrapperName("stellar_wrap_gameui.player-hp"));
        Assert.False(WrapperPath.IsWrapperName("node_stellar_wrap_x"));
        Assert.False(WrapperPath.IsWrapperName(null));
    }
}
