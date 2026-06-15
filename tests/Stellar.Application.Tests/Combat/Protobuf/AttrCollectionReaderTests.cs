using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class AttrCollectionReaderTests
{
    // Confirmed against (local reference)
    // (identical to (local reference)):
    //   AttrName  = 1
    //   AttrId    = 10
    //   AttrPos   = 52
    //   AttrHp    = 11310
    //   AttrMaxHp = 11320
    private const int AttrHpId    = 11310;
    private const int AttrMaxHpId = 11320;

    [Fact]
    public void TryRead_SingleHpAttr_ParsesValue()
    {
        var rawData = new WireBytes().Varint(4321).ToArray();    // varint(4321)
        var attrBytes = new WireBytes()
            .Tag(1, 0).Varint((ulong)AttrHpId)
            .Tag(2, 2).LengthDelimited(rawData)
            .ToArray();
        var payload = new WireBytes()
            .Tag(1, 0).Varint(1234567890UL)        // Uuid
            .Tag(2, 2).LengthDelimited(attrBytes)  // Attr
            .ToArray();

        var ok = AttrCollectionReader.TryRead(payload, out var attrs);

        Assert.True(ok);
        Assert.Equal(1234567890L, attrs.Uuid);
        Assert.Single(attrs.Items);
        Assert.Equal(AttrHpId, attrs.Items[0].Id);
        Assert.Equal(4321, attrs.Items[0].DecodedInt);
    }

    [Fact]
    public void TryRead_MultipleAttrs_PreservesOrder()
    {
        var hpRaw    = new WireBytes().Varint(100).ToArray();
        var maxHpRaw = new WireBytes().Varint(1000).ToArray();
        var hpAttr   = new WireBytes().Tag(1, 0).Varint((ulong)AttrHpId).Tag(2, 2).LengthDelimited(hpRaw).ToArray();
        var maxAttr  = new WireBytes().Tag(1, 0).Varint((ulong)AttrMaxHpId).Tag(2, 2).LengthDelimited(maxHpRaw).ToArray();
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(hpAttr)
            .Tag(2, 2).LengthDelimited(maxAttr)
            .ToArray();

        var ok = AttrCollectionReader.TryRead(payload, out var attrs);

        Assert.True(ok);
        Assert.Equal(2, attrs.Items.Count);
        Assert.Equal(AttrHpId,    attrs.Items[0].Id);
        Assert.Equal(AttrMaxHpId, attrs.Items[1].Id);
    }

    [Fact]
    public void TryRead_EmptyPayload_ReturnsTrueWithNoItems()
    {
        var ok = AttrCollectionReader.TryRead(new byte[0], out var attrs);
        Assert.True(ok);
        Assert.Empty(attrs.Items);
        Assert.Equal(0L, attrs.Uuid);
    }

    [Fact]
    public void TryRead_MalformedAttrInsideRepeatedField_ReturnsFalse()
    {
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(new byte[] { 0x08 })   // tag only, no value
            .ToArray();

        var ok = AttrCollectionReader.TryRead(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void DecodedString_RoundTripsUtf8()
    {
        // AttrName (EAttrType=1) ships the raw UTF-8 display name in Attr.RawData
        // without an inner length prefix. The accessor must round-trip arbitrary
        // text — including non-ASCII codepoints common in CN/JP/KR character
        // names — without mangling.
        const int AttrNameId = 1;
        const string original = "Doraemonドラえもん";

        var rawBytes = System.Text.Encoding.UTF8.GetBytes(original);
        var attrBytes = new WireBytes()
            .Tag(1, 0).Varint((ulong)AttrNameId)
            .Tag(2, 2).LengthDelimited(rawBytes)
            .ToArray();
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(attrBytes)
            .ToArray();

        var ok = AttrCollectionReader.TryRead(payload, out var attrs);

        Assert.True(ok);
        var item = Assert.Single(attrs.Items);
        Assert.Equal(AttrNameId, item.Id);
        Assert.Equal(original, item.DecodedString);
    }
}
