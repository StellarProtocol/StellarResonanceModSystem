using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// Pre-Phase-4 SRP split: name-cache concerns formerly inside
/// <c>CombatService</c> live in <c>EntityNameRegistry</c> now. These four
/// tests pin the contract — the prior CombatService tests covering the
/// same surface (UpdateEntityName / GetEntityName / OnEntityDisappeared)
/// keep the integration path covered.
/// </summary>
public sealed class EntityNameRegistryTests
{
    private static readonly EntityId Sample = new(0x0000_0001_0000_5188L);

    [Fact]
    public void SetThenGet_ReturnsName()
    {
        var registry = new EntityNameRegistry();
        Assert.Null(registry.Get(Sample));

        registry.Set(Sample, "Doraemon");

        Assert.Equal("Doraemon", registry.Get(Sample));
    }

    /// <summary>
    /// Production behavior (mirrors the prior <c>CombatService.UpdateEntityName</c>
    /// guard): null/empty inputs silently early-return so a transient
    /// AttrName row carrying an empty string can't clobber a previously
    /// resolved name. The test asserts NO throw and NO store.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Set_WithNullOrEmpty_IsSilentlyIgnored(string? name)
    {
        var registry = new EntityNameRegistry();

        registry.Set(Sample, name!);

        Assert.Null(registry.Get(Sample));
    }

    [Fact]
    public void Evict_RemovesExistingName()
    {
        var registry = new EntityNameRegistry();
        registry.Set(Sample, "Doraemon");
        Assert.Equal("Doraemon", registry.Get(Sample));

        registry.Evict(Sample);

        Assert.Null(registry.Get(Sample));
    }

    [Fact]
    public void Evict_UnknownId_IsNoOp()
    {
        var registry = new EntityNameRegistry();
        var unknown = new EntityId(0x0000_0009_0000_0040L);

        // No throw, no observable state change.
        registry.Evict(unknown);
        Assert.Null(registry.Get(unknown));
    }
}
