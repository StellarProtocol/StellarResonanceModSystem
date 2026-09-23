using System.Collections.Generic;
using Stellar.Infrastructure.UI;
using Xunit;

namespace Stellar.Application.Tests;

/// <summary>Pins the Settings → Hotkeys natural sort: a numbered action like "loadout.apply.10" sorts
/// AFTER "…apply.9", not between .1 and .2 (owner report 2026-09-22 — loadout 10 shortcut appeared
/// under loadout 1).</summary>
public sealed class NaturalOrderTests
{
    [Fact]
    public void NumberedHotkeyIds_SortNumericallyNotLexically()
    {
        var ids = new List<string>
        {
            "loadout.apply.1", "loadout.apply.10", "loadout.apply.2", "loadout.apply.9",
            "loadout.apply.3", "loadout.apply.8",
        };
        ids.Sort(NaturalOrder.Compare);
        Assert.Equal(
            new[] { "loadout.apply.1", "loadout.apply.2", "loadout.apply.3", "loadout.apply.8", "loadout.apply.9", "loadout.apply.10" },
            ids);
    }

    [Theory]
    [InlineData("apply.9", "apply.10", -1)]   // 9 before 10
    [InlineData("apply.10", "apply.2", 1)]    // 10 after 2
    [InlineData("apply.2", "apply.10", -1)]
    [InlineData("apply.1", "apply.1", 0)]     // equal
    [InlineData("apply.01", "apply.1", 1)]    // equal value, more leading zeros sorts after
    [InlineData("abc", "abd", -1)]            // pure text still ordinal
    [InlineData("a2b", "a10b", -1)]           // numeric run mid-string, text tail identical
    public void PairwiseOrder(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, System.Math.Sign(NaturalOrder.Compare(a, b)));
        Assert.Equal(-expectedSign, System.Math.Sign(NaturalOrder.Compare(b, a)));   // antisymmetric
    }
}
