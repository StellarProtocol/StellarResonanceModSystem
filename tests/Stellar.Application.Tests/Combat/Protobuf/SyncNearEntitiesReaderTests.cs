using Stellar.Wire;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class SyncNearEntitiesReaderTests
{
    [Fact]
    public void TryReadDisappearedUuids_TwoEntries_BothExtracted()
    {
        var d1 = new WireBytes().Tag(1, 0).Varint(0x111UL).ToArray();
        var d2 = new WireBytes().Tag(1, 0).Varint(0x222UL).ToArray();
        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(new byte[] { 0x08, 0xAB })  // appear entry (ignored)
            .Tag(2, 2).LengthDelimited(d1)
            .Tag(2, 2).LengthDelimited(d2)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadDisappearedUuids(payload, out var uuids);

        Assert.True(ok);
        Assert.Equal(new[] { 0x111L, 0x222L }, uuids);
    }

    [Fact]
    public void TryReadDisappearedUuids_NoDisappearField_ReturnsEmpty()
    {
        var ok = SyncNearEntitiesReader.TryReadDisappearedUuids(new byte[0], out var uuids);
        Assert.True(ok);
        Assert.Empty(uuids);
    }

    // -----------------------------------------------------------------------
    // TryReadAppearAndDisappear — Phase 3b name-resolution path. The combat
    // probe parses appear entities so it can extract AttrName from each
    // entity's AttrCollection on first sight, before any delta arrives.
    // -----------------------------------------------------------------------

    [Fact]
    public void TryReadAppearAndDisappear_AppearOnly_ExtractsUuids()
    {
        var appear1 = new WireBytes().Tag(1, 0).Varint(0xAAAAUL).ToArray();
        var appear2 = new WireBytes().Tag(1, 0).Varint(0xBBBBUL).ToArray();
        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(appear1)
            .Tag(1, 2).LengthDelimited(appear2)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out var disappears);

        Assert.True(ok);
        Assert.Equal(2, appears.Count);
        Assert.Equal(0xAAAAL, appears[0].Uuid);
        Assert.Null(appears[0].Attrs);
        Assert.Equal(0xBBBBL, appears[1].Uuid);
        Assert.Null(appears[1].Attrs);
        Assert.Empty(disappears);
    }

    [Fact]
    public void TryReadAppearAndDisappear_DisappearOnly_ExtractsUuids()
    {
        var d1 = new WireBytes().Tag(1, 0).Varint(0x111UL).ToArray();
        var d2 = new WireBytes().Tag(1, 0).Varint(0x222UL).ToArray();
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(d1)
            .Tag(2, 2).LengthDelimited(d2)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out var disappears);

        Assert.True(ok);
        Assert.Empty(appears);
        Assert.Equal(new[] { 0x111L, 0x222L }, disappears);
    }

    [Fact]
    public void TryReadAppearAndDisappear_Mixed_ParsesBothLists()
    {
        var appear = new WireBytes().Tag(1, 0).Varint(0xAAAAUL).ToArray();
        var d1     = new WireBytes().Tag(1, 0).Varint(0x111UL).ToArray();
        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(appear)
            .Tag(2, 2).LengthDelimited(d1)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out var disappears);

        Assert.True(ok);
        Assert.Single(appears);
        Assert.Equal(0xAAAAL, appears[0].Uuid);
        Assert.Single(disappears);
        Assert.Equal(0x111L, disappears[0]);
    }

    [Fact]
    public void TryReadAppearAndDisappear_EntityWithoutAttrCollection_AttrsIsNull()
    {
        // Edge case: appear entity that carries only uuid (no field 3 sub-msg).
        // Older builds and minimal NPCs omit AttrCollection; the parser must
        // still surface the uuid and leave Attrs at null so the consumer can
        // treat it as "no name yet."
        var entityBytes = new WireBytes()
            .Tag(1, 0).Varint(0xDEADBEEFUL)
            .ToArray();

        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(entityBytes)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out var disappears);

        Assert.True(ok);
        var appear = Assert.Single(appears);
        Assert.Equal(0x0DEADBEEFL, appear.Uuid);
        Assert.Null(appear.Attrs);
        Assert.Empty(disappears);
    }

    [Fact]
    public void TryReadAppearAndDisappear_MultipleTopLevelField1Entities_AllParsed()
    {
        // Defensive parse: some senders may pack multiple Entity entries into
        // the same top-level Field-1 wire stream. The parser is a flat tag
        // loop, so this just looks like three independent field-1 occurrences
        // — each must surface as its own AppearEntityMsg.
        var e1 = new WireBytes().Tag(1, 0).Varint(0xAAAAUL).ToArray();
        var e2 = new WireBytes().Tag(1, 0).Varint(0xBBBBUL).ToArray();
        var e3 = new WireBytes().Tag(1, 0).Varint(0xCCCCUL).ToArray();

        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(e1)
            .Tag(1, 2).LengthDelimited(e2)
            .Tag(1, 2).LengthDelimited(e3)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out var disappears);

        Assert.True(ok);
        Assert.Equal(3, appears.Count);
        Assert.Equal(0xAAAAL, appears[0].Uuid);
        Assert.Equal(0xBBBBL, appears[1].Uuid);
        Assert.Equal(0xCCCCL, appears[2].Uuid);
        Assert.Empty(disappears);
    }

    [Fact]
    public void TryReadAppearAndDisappear_AppearWithAttrName_DecodesName()
    {
        // Inner Attr: id=AttrName(1), raw_data="Doraemon"
        const string expectedName = "Doraemon";
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(expectedName);
        var attrBytes = new WireBytes()
            .Tag(1, 0).Varint((ulong)AttrTypeIds.AttrName)
            .Tag(2, 2).LengthDelimited(nameBytes)
            .ToArray();

        // AttrCollection { uuid=0xC0FFEE, attrs=[Attr{ ... }] }
        var attrCollectionBytes = new WireBytes()
            .Tag(1, 0).Varint(0xC0FFEEUL)
            .Tag(2, 2).LengthDelimited(attrBytes)
            .ToArray();

        // Entity { uuid=0xC0FFEE, attrs=AttrCollection{...} }
        // Include a wire field we don't consume (ent_type, field 2 varint) to
        // make sure the parser skips unknown fields correctly.
        var entityBytes = new WireBytes()
            .Tag(1, 0).Varint(0xC0FFEEUL)
            .Tag(2, 0).Varint(7UL)  // ent_type (skipped)
            .Tag(3, 2).LengthDelimited(attrCollectionBytes)
            .ToArray();

        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(entityBytes)
            .ToArray();

        var ok = SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out var disappears);

        Assert.True(ok);
        var appear = Assert.Single(appears);
        Assert.Equal(0xC0FFEEL, appear.Uuid);
        Assert.NotNull(appear.Attrs);
        var attrs = appear.Attrs!.Value;
        Assert.Equal(0xC0FFEEL, attrs.Uuid);
        var item = Assert.Single(attrs.Items);
        Assert.Equal(AttrTypeIds.AttrName, item.Id);
        Assert.Equal(expectedName, item.DecodedString);
        Assert.Empty(disappears);
    }
}
