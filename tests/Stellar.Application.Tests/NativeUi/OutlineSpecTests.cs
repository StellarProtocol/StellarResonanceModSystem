using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.UI;
using Xunit;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>The allowlist RectChild outline-spec parser (pure, no Unity): existing ';' / '*' / 'h:' / 'w:' syntax is
/// unchanged and '-' marks an EXCLUDE token.</summary>
public sealed class OutlineSpecTests
{
    [Fact]
    public void QuickbarSpec_ContentUnionMinusDrawer()
    {
        var t = OutlineSpec.Parse("*button_pos_group;-button_pos_group/10");
        Assert.Equal(2, t.Count);
        Assert.Equal(new OutlineToken("button_pos_group", Content: true, AxisX: true, AxisY: true, Exclude: false), t[0]);
        Assert.Equal(new OutlineToken("button_pos_group/10", Content: false, AxisX: false, AxisY: false, Exclude: true), t[1]);
    }

    [Fact]
    public void ExistingSyntax_IsUnchanged()
    {
        var t = OutlineSpec.Parse("node_track_title; h:node_track_sub/x ;w:*a/b;*.");
        Assert.Equal(new OutlineToken("node_track_title", false, true, true, false), t[0]);
        Assert.Equal(new OutlineToken("node_track_sub/x", false, false, true, false), t[1]);
        Assert.Equal(new OutlineToken("a/b", true, true, false, false), t[2]);
        Assert.Equal(new OutlineToken(".", true, true, true, false), t[3]);
    }

    [Fact] // Every allowlist spec parses; only the quickbar uses an exclude, and no entry is excluded-only.
    public void AllAllowlistSpecs_Parse()
    {
        foreach (var e in NativeUiAllowlist.V1Targets)
        {
            if (e.RectChild is null) continue;
            var tokens = OutlineSpec.Parse(e.RectChild);
            Assert.Contains(tokens, tok => !tok.Exclude);
        }
    }
}
