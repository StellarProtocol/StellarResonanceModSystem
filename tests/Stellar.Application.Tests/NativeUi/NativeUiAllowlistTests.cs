using System.Linq;
using System.Text.Json;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.UI;
using Xunit;

namespace Stellar.Application.Tests.NativeUi;

/// <summary>Shape checks on the hand-curated native-UI allowlist: ids unique, every entry named in every shipped
/// framework language, and twin entries (nodes sharing one outer wrapper) only where the adapter supports them.</summary>
public sealed class NativeUiAllowlistTests
{
    [Fact]
    public void Ids_AreUnique_AndUnderGameUi()
    {
        var ids = NativeUiAllowlist.V1Targets.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("gameui.", id));
    }

    [Fact] // The location + line bar: outer-wrapped, content outline, its recycle notice twin shares the wrapper.
    public void LocationLine_IsOuterWrapped_WithRecycleTwin()
    {
        var e = Assert.Single(NativeUiAllowlist.V1Targets, x => x.Id == "gameui.location-line");
        Assert.Equal("gameui.entry.locationLine", e.DisplayNameKey);
        Assert.EndsWith("/node_upper_left/group_sceneline", e.Path);
        Assert.Equal(NativeUiWrapMode.Outer, e.Wrap);
        Assert.Equal("*.", e.RectChild);
        Assert.Equal(new[] { "group_sceneline_recycle" }, e.Twins);
        Assert.True(e.SafeToHide);
        Assert.True(e.DefaultFraction.Width > 0f && e.DefaultFraction.Height > 0f);
    }

    [Fact] // Quickbar: last-seen only at the skill wheel's rest scale (0.52; 1.00 while L Ctrl holds it expanded), and
           // v2 seen keys so the expanded rect saved before the gate is ignored.
    public void Quickbar_SeenGate_OnCollapsedSkillWheel_AndV2SeenKeys()
    {
        var e = Assert.Single(NativeUiAllowlist.V1Targets, x => x.Id == "gameui.quickbar");
        Assert.NotNull(e.SeenGate);
        Assert.EndsWith("/battle_main_node_window_skill_drawer_expand_pc", e.SeenGate!.Path);
        Assert.InRange(e.SeenGate.MaxScale, 0.53f, 0.99f);
        Assert.Equal(2, e.SeenKeyVersion);
        Assert.All(NativeUiAllowlist.V1Targets.Where(x => x.Id != "gameui.quickbar"), x => Assert.Equal(1, x.SeenKeyVersion));
    }

    [Fact] // Twins join an OUTER wrapper (siblings of the element); an inner wrapper lives under the element.
    public void Twins_OnlyOnOuterEntries()
    {
        foreach (var e in NativeUiAllowlist.V1Targets.Where(x => x.Twins is { Length: > 0 }))
        {
            Assert.Equal(NativeUiWrapMode.Outer, e.Wrap);
            Assert.All(e.Twins!, n => Assert.DoesNotContain("/", n));
        }
    }

    [Fact] // Rule: every user-facing key in EVERY framework language catalog (embedded Lang/*.json).
    public void EveryDisplayNameKey_InEveryLanguage()
    {
        var asm = typeof(NativeUiAllowlist).Assembly;
        var langs = asm.GetManifestResourceNames().Where(n => n.Contains(".Lang.") && n.EndsWith(".json")).ToList();
        Assert.NotEmpty(langs);
        foreach (var res in langs)
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var doc = JsonDocument.Parse(s);
            foreach (var e in NativeUiAllowlist.V1Targets)
            {
                Assert.True(doc.RootElement.TryGetProperty(e.DisplayNameKey, out var v), $"{res} lacks {e.DisplayNameKey}");
                Assert.False(string.IsNullOrWhiteSpace(v.GetString()), $"{res}: {e.DisplayNameKey} is empty");
            }
        }
    }
}
