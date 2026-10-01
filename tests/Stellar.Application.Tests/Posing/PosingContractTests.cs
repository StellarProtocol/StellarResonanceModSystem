using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Pins the 2.15.0 posing surface (spec 2026-10-02-photo-studio-posing-design.md § 5): the 8-member interface budget,
// the version, and the enum names plugins switch on.
public sealed class PosingContractTests
{
    private static int Members(Type t) =>
        t.GetMethods().Count(m => !m.IsSpecialName) + t.GetProperties().Length + t.GetEvents().Length;

    [Theory]
    [InlineData(typeof(IPosing), 7)]
    [InlineData(typeof(IPoseTarget), 8)]
    public void Contract_member_counts_are_pinned(Type contract, int expected)
    {
        Assert.Equal(expected, Members(contract));
        Assert.True(Members(contract) <= 8, $"{contract.Name} exceeds the 8-member budget");
    }

    [Fact]
    public void Framework_version_is_2_15_0() => Assert.Equal("2.15.0", FrameworkVersion.Value);

    [Fact]
    public void Enum_names_are_stable()
    {
        Assert.Equal(new[] { "Self", "Player", "Npc" }, Enum.GetNames(typeof(PersonKind)));
        Assert.Equal(new[] { "Default", "Lens", "Free" }, Enum.GetNames(typeof(LookMode)));
        Assert.Equal(new[] { "Head", "Eyes" }, Enum.GetNames(typeof(LookPart)));
        Assert.Equal(new[] { "Idle", "Loading", "Ready", "Failed", "Released", "Full" }, Enum.GetNames(typeof(PoseTargetState)));
        Assert.Equal(new[] { "Applied", "Loading", "Refused", "Unavailable", "Full" }, Enum.GetNames(typeof(PoseResult)));
    }
}
