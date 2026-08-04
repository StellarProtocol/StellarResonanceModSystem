using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Covers <see cref="PandaCharIdentityReader"/>'s walk of the live
/// <c>CharSerialize</c> record. The reader is pure reflection over duck-typed
/// objects, so the real proto shape can be stood in for by POCOs here — no
/// IL2CPP / BepInEx host needed.
///
/// <para>PINNED: <see cref="PrefersCurProfessionIdOverInitProfessionId"/>. The
/// record exposes BOTH <c>CharBaseInfo.InitProfessionId</c> (the character's
/// INITIAL class) and <c>ProfessionList.CurProfessionId</c> (the current one).
/// Reading the former ships the wrong class crest for anyone who has switched
/// class — the owner's own character reads profession 2 on some launches and 5 on
/// others. Do not "simplify" this to the CharBase field.</para>
/// </summary>
public sealed class PandaCharIdentityReaderTests
{
    // ── property-backed shapes (what the live assembly exposes) ──
    private sealed class CharBaseInfo
    {
        public string? Name { get; set; }
        public int InitProfessionId { get; set; }
    }

    private sealed class RoleLevel
    {
        public int Level { get; set; }
    }

    private sealed class ProfessionList
    {
        public int CurProfessionId { get; set; }
    }

    private sealed class CharSerialize
    {
        public long CharId { get; set; }
        public CharBaseInfo? CharBase { get; set; }
        public RoleLevel? RoleLevel { get; set; }
        public ProfessionList? ProfessionList { get; set; }
    }

    // ── field-backed shape (what the cpp2il dump renders) ──
    private sealed class FieldCharBase
    {
        public string? Name;
    }

    private sealed class FieldCharSerialize
    {
        public long CharId;
        public FieldCharBase? CharBase;
    }

    private static CharSerialize Revette() => new()
    {
        CharId = 1248014,
        CharBase = new CharBaseInfo { Name = "Revette", InitProfessionId = 1 },
        RoleLevel = new RoleLevel { Level = 60 },
        ProfessionList = new ProfessionList { CurProfessionId = 2 },
    };

    private static PandaCharIdentityReader ReaderOver(object? record)
        => new(new StubLog(), () => record);

    [Fact]
    public void ReadsTheIdentityChain()
    {
        var reader = ReaderOver(Revette());

        Assert.True(reader.TryRead(out var identity));
        Assert.Equal(1248014L, identity.CharId);
        Assert.Equal("Revette", identity.Name);
        Assert.Equal(60, identity.Level);
        Assert.Equal(2, identity.Profession);
    }

    [Fact]
    public void PrefersCurProfessionIdOverInitProfessionId()
    {
        var record = Revette();
        record.CharBase!.InitProfessionId = 1;   // initial class
        record.ProfessionList!.CurProfessionId = 5;   // current class

        var reader = ReaderOver(record);

        Assert.True(reader.TryRead(out var identity));
        Assert.Equal(5, identity.Profession);
    }

    [Fact]
    public void ResolvesFieldBackedMembers()
    {
        var reader = ReaderOver(new FieldCharSerialize
        {
            CharId = 4242,
            CharBase = new FieldCharBase { Name = "FieldHero" },
        });

        Assert.True(reader.TryRead(out var identity));
        Assert.Equal("FieldHero", identity.Name);
        Assert.Equal(4242L, identity.CharId);
    }

    [Fact]
    public void ReturnsFalseWhileTheRecordIsUnavailable()
    {
        var reader = ReaderOver(null);

        Assert.False(reader.TryRead(out var identity));
        Assert.Null(identity.Name);
    }

    [Fact]
    public void ReturnsFalseWhenTheRecordCarriesNothingUsable()
    {
        // Present but empty (pre-sync): must not publish a blank identity, or the
        // service would treat "" as a known name.
        var reader = ReaderOver(new CharSerialize());

        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void ToleratesMissingChildren()
    {
        // CharBase absent, level present — the readable half must still come back.
        var reader = ReaderOver(new CharSerialize
        {
            CharId = 7,
            CharBase = null,
            RoleLevel = new RoleLevel { Level = 42 },
        });

        Assert.True(reader.TryRead(out var identity));
        Assert.Null(identity.Name);
        Assert.Equal(42, identity.Level);
    }

    [Fact]
    public void PicksUpTheRecordOnceItLands()
    {
        CharSerialize? record = null;
        var reader = new PandaCharIdentityReader(new StubLog(), () => record);

        Assert.False(reader.TryRead(out _));

        record = Revette();

        Assert.True(reader.TryRead(out var identity));
        Assert.Equal("Revette", identity.Name);
    }

    [Fact]
    public void KeepsServingTheLastIdentityWhenTheRecordGoesAway()
    {
        object? record = Revette();
        var reader = new PandaCharIdentityReader(new StubLog(), () => record);
        Assert.True(reader.TryRead(out _));

        // Scene teardown drops the record; identity must not evaporate.
        record = null;

        Assert.True(reader.TryRead(out var identity));
        Assert.Equal("Revette", identity.Name);
        Assert.Equal(2, identity.Profession);
    }

    [Fact]
    public void DoesNotHitTheSourceOnEveryCallOnceCached()
    {
        // PINNED (perf): this reader runs on the per-tick service refresh (~30Hz).
        // Re-walking the record every tick is waste, and the source delegate
        // reaches into the inventory reader — so the cache must absorb the bulk of
        // the calls. Guards against someone "simplifying" the recheck away.
        var calls = 0;
        object? record = Revette();
        var reader = new PandaCharIdentityReader(new StubLog(), () => { calls++; return record; });

        for (var i = 0; i < 200; i++)
        {
            Assert.True(reader.TryRead(out _));
        }

        Assert.True(calls < 20, $"source hit {calls} times across 200 reads — cache is not absorbing calls");
    }

    [Fact]
    public void SurvivesAThrowingAccessor()
    {
        var reader = new PandaCharIdentityReader(
            new StubLog(), () => throw new System.InvalidOperationException("boom"));

        Assert.False(reader.TryRead(out _));
    }
}
