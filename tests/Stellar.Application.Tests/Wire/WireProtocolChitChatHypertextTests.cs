using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>ChatMsgInfo field 7 (<c>ChatHypertext</c>) and field 6 (<c>ChatVoice</c>), hand-built from the protos in
/// <c>proto\zproto\stru_place_holder*.proto</c> / <c>stru_chat_voice.proto</c>.</summary>
public sealed class WireProtocolChitChatHypertextTests
{
    // PlaceHolder{ type = 1 (enum varint); bytes_content = 2 }
    private static byte[] Slot(int type, byte[] body)
        => new WireBytes().Tag(1, 0).Varint((ulong)type).Tag(2, 2).LengthDelimited(body).ToArray();

    private static byte[] StrSlot(string text) => Slot(7, new WireBytes().Tag(1, 2).String(text).ToArray());

    // int32 negatives go on the wire as 10-byte two's-complement varints.
    private static ulong Int32(int v) => unchecked((ulong)(long)v);

    private static byte[] PositionSlot(int scene, int line, (int X, int Y, int Z) p, int layer, string? guid)
    {
        var b = new WireBytes().Tag(1, 0).Varint(Int32(scene)).Tag(2, 0).Varint(Int32(line))
            .Tag(3, 0).Varint(Int32(p.X)).Tag(4, 0).Varint(Int32(p.Y)).Tag(5, 0).Varint(Int32(p.Z))
            .Tag(6, 0).Varint(Int32(layer));
        if (guid is not null) b.Tag(7, 2).String(guid);
        return Slot(13, b.ToArray());
    }

    private static byte[] Hypertext(uint configId, params byte[][] slots)
    {
        var w = new WireBytes().Tag(1, 0).Varint(configId);
        foreach (var s in slots) w.Tag(2, 2).LengthDelimited(s);
        return w.ToArray();
    }

    // ChatMsgInfo{ msg_type = 1; chat_hypertext = 7 } / { msg_type = 1; voice = 6 }
    private static byte[] MsgInfo(int msgType, int field, byte[] body)
        => new WireBytes().Tag(1, 0).Varint((ulong)msgType).Tag(field, 2).LengthDelimited(body).ToArray();

    [Fact]
    public void PositionShare_DecodesStringsAndScenePosition()
    {
        var ht = Hypertext(3000002,
            StrSlot("meet here "),
            PositionSlot(9, 3, (1203, 88, -455), 2, "a1b2-guid"),
            StrSlot(" pls"));

        var f = WireProtocol.ParseChatMsgInfo(MsgInfo(6, 7, ht));

        Assert.Equal(6, f.MsgType);
        Assert.NotNull(f.Hypertext);
        Assert.Equal(3000002, f.Hypertext!.ConfigId);
        Assert.Equal(3, f.Hypertext.Placeholders.Count);
        Assert.Equal(ChatPlaceholderType.String, f.Hypertext.Placeholders[0].Type);
        Assert.Equal("meet here ", f.Hypertext.Placeholders[0].Text);
        var pos = f.Hypertext.Placeholders[1].Position;
        Assert.NotNull(pos);
        Assert.Equal(9, pos!.SceneId);
        Assert.Equal(3, pos.LineId);
        Assert.Equal(1203, pos.X);
        Assert.Equal(88, pos.Y);
        Assert.Equal(-455, pos.Z);                  // negative int32 survives the 10-byte varint
        Assert.Equal(2, pos.MapLayerId);
        Assert.Equal("a1b2-guid", pos.SceneGuid);   // S4 field 7
        Assert.Equal(" pls", f.Hypertext.Placeholders[2].Text);
        Assert.Empty(f.Hypertext.Segments);         // segments are built later, on the main thread
    }

    [Fact]
    public void LootRoll_DecodesPlayerItemAndVal()
    {
        var player = Slot(2, new WireBytes().Tag(1, 0).Varint(5_532_317_001UL).Tag(2, 2).String("Mahiru").ToArray());
        var item = Slot(3, new WireBytes().Tag(1, 0).Varint(1_013_001).ToArray());
        var val = Slot(1, new WireBytes().Tag(1, 0).Varint(87).ToArray());

        var ht = WireProtocol.ParseChatHypertext(Hypertext(1043010, player, val, item));

        Assert.NotNull(ht);
        Assert.Equal(1043010, ht!.ConfigId);
        Assert.Collection(ht.Placeholders,
            p => { Assert.Equal(ChatPlaceholderType.Player, p.Type); Assert.Equal(5_532_317_001L, p.CharId); Assert.Equal("Mahiru", p.Text); },
            p => { Assert.Equal(ChatPlaceholderType.Val, p.Type); Assert.Equal(87L, p.Value); },
            p => { Assert.Equal(ChatPlaceholderType.Item, p.Type); Assert.Equal(1_013_001L, p.Value); });
    }

    [Fact]
    public void Voice_DecodesSecondsAndTranscript()
    {
        var voice = new WireBytes().Tag(1, 2).String("cos/abc").Tag(2, 2).String("pull in five").Tag(3, 0).Varint(5).ToArray();

        var f = WireProtocol.ParseChatMsgInfo(MsgInfo(5, 6, voice));

        Assert.Equal(5, f.MsgType);
        Assert.NotNull(f.Voice);
        Assert.Equal(5, f.Voice!.Seconds);
        Assert.Equal("pull in five", f.Voice.Transcript);
        Assert.Equal("cos/abc", f.Voice.FileId);
        Assert.Null(f.Hypertext);
    }

    [Fact]
    public void Notify_CarriesHypertextOntoChatMessage()
    {
        var info = MsgInfo(6, 7, Hypertext(3000002, PositionSlot(9, 1, (1, 2, 3), 0, null)));
        var chitChatMsg = new WireBytes().Tag(1, 0).Varint(42).Tag(3, 0).Varint(1_700_000_000).Tag(4, 2).LengthDelimited(info).ToArray();
        var notify = new WireBytes().Tag(1, 0).Varint(1).Tag(2, 2).LengthDelimited(chitChatMsg).ToArray();

        Assert.True(WireProtocol.TryParseChitChatNotify(notify, out var msg, out _));

        Assert.Equal(ChatMessageType.ItemLink, msg!.Type);
        Assert.Equal(string.Empty, msg.Text);
        Assert.Equal(3000002, msg.Hypertext!.ConfigId);
        Assert.Null(msg.Hypertext.Placeholders[0].Position!.SceneGuid);
    }

    [Fact]
    public void UnknownPlaceholderType_IsKeptAsBareSlot()
    {
        var ht = WireProtocol.ParseChatHypertext(Hypertext(10000001, Slot(99, new byte[] { 0x08, 0x01 }), StrSlot("x")));

        Assert.Equal(2, ht!.Placeholders.Count);
        Assert.Equal(ChatPlaceholderType.None, ht.Placeholders[0].Type);
        Assert.Equal("x", ht.Placeholders[1].Text);
    }

    [Fact]
    public void MalformedBlobs_NeverThrow_AndKeepWhatWasRead()
    {
        // Truncated: a good String slot, then a slot whose length prefix runs past the end.
        var good = Hypertext(3000002, StrSlot("ok"));
        var truncated = new byte[good.Length + 3];
        good.CopyTo(truncated, 0);
        truncated[good.Length] = 0x12;       // tag field 2, wire 2
        truncated[good.Length + 1] = 0x7F;   // claims 127 bytes
        truncated[good.Length + 2] = 0x01;

        var ht = WireProtocol.ParseChatHypertext(truncated);
        Assert.Equal("ok", Assert.Single(ht!.Placeholders).Text);

        var rng = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rng.Next(0, 64)];
            rng.NextBytes(junk);
            var f = WireProtocol.ParseChatMsgInfo(junk);    // must not throw
            Assert.NotNull(f.Text);
            WireProtocol.ParseChatHypertext(junk);
            WireProtocol.ParseChatVoice(junk);
        }

        // A PlaceHolder whose body is garbage still yields a typed (empty) slot.
        var bad = WireProtocol.ParseChatHypertext(Hypertext(1, Slot(13, new byte[] { 0x0A, 0xFF })));
        Assert.Equal(ChatPlaceholderType.ScenePosition, bad!.Placeholders[0].Type);
        Assert.Equal(0, bad.Placeholders[0].Position!.SceneId);
    }

    [Fact]
    public void LegacyOverload_StillReadsTextStickerTarget()
    {
        var bytes = new WireBytes().Tag(1, 0).Varint(0).Tag(2, 0).Varint(77).Tag(3, 2).String("hi")
            .Tag(7, 2).LengthDelimited(Hypertext(5, StrSlot("x"))).ToArray();

        WireProtocol.TryParseChatMsgInfo(bytes, out var text, out var type, out var sticker, out var target);

        Assert.Equal("hi", text);
        Assert.Equal(0, type);
        Assert.Equal(0, sticker);
        Assert.Equal(77L, target);
    }
}
