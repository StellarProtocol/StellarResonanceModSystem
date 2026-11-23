using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Layout of hypertext messages into text/link segments, against the live-table shapes in
/// <c>tables\ChatHyperlink.json</c> (fake lookup, no game).</summary>
public sealed class ChatHypertextBuilderTests
{
    private sealed class FakeNames : IChatNameLookup
    {
        public Dictionary<int, ChatHyperlinkRow> Rows { get; } = new();
        public Dictionary<int, (string Name, int Quality)> Items { get; } = new();
        public Dictionary<int, string> Scenes { get; } = new();

        public ChatHyperlinkRow? GetHyperlink(int configId) => Rows.TryGetValue(configId, out var r) ? r : null;

        public bool TryGetItem(int itemId, out string name, out int quality)
        {
            var ok = Items.TryGetValue(itemId, out var it);
            name = ok ? it.Name : string.Empty;
            quality = ok ? it.Quality : 0;
            return ok;
        }

        public string? GetSceneName(int sceneId) => Scenes.TryGetValue(sceneId, out var s) ? s : null;
        public string? GetBuffName(int buffId) => null;
        public string? GetUnionBuildingName(int buildingId) => null;

        public void Row(int id, string content, string button = "") => Rows[id] = new ChatHyperlinkRow(id, content, 0, button);
    }

    private static ChatPlaceholder Str(string t) => new() { Type = ChatPlaceholderType.String, Text = t };
    private static ChatPlaceholder Player(long id, string name) => new() { Type = ChatPlaceholderType.Player, CharId = id, Text = name };
    private static ChatPlaceholder Item(int id) => new() { Type = ChatPlaceholderType.Item, Value = id };
    private static ChatPlaceholder Val(long v) => new() { Type = ChatPlaceholderType.Val, Value = v };

    private static ChatPlaceholder Pos(int scene) => new()
    {
        Type = ChatPlaceholderType.ScenePosition,
        Position = new ChatPosition { SceneId = scene, LineId = 3, X = 1203, Y = 88, Z = -455, MapLayerId = 2, SceneGuid = "g" },
    };

    private static ChatHypertext Raw(int configId, params ChatPlaceholder[] slots)
        => new() { ConfigId = configId, Placeholders = slots };

    private static string Shape(ChatHypertext h) => string.Join("|", h.Segments.Select(s => $"{s.Kind}:{s.Text}"));

    [Fact]
    public void PositionShare_LiveTemplate_SplitsTextLinkText()
    {
        var names = new FakeNames();
        names.Row(3000002, "{*string[1]*}[ <u><link=1>{*scence.name*} Line {*val1*} ({*val2*}, {*val3*}, {*val4*})</link></u> ]{*string[2]*}");
        names.Scenes[9] = "Asterleeds";

        var h = new ChatHypertextBuilder(names).Build(Raw(3000002, Str("meet <color=red>here</color> "), Pos(9), Str(" ok")), "Sender", "");

        Assert.Equal(ChatLinkKind.Position, h.Kind);
        Assert.True(h.IsShare);
        Assert.Equal("Text:meet here [ |Position:Asterleeds Line 3 (1203, 88, -455)|Text: ] ok", Shape(h));
        var link = h.Segments[1];
        Assert.Equal(9, link.Position!.SceneId);
        Assert.Equal("Asterleeds", link.Position.SceneName);
        Assert.Equal("g", link.Position.SceneGuid);
        Assert.Equal("Asterleeds", h.Placeholders[1].Position!.SceneName);   // raw slots get the name too
        Assert.Equal("meet here [ Asterleeds Line 3 (1203, 88, -455) ] ok", h.PlainText);
    }

    [Fact]
    public void PositionShare_NoTables_UsesFallback()
    {
        var h = new ChatHypertextBuilder(NullChatNameLookup.Instance).Build(Raw(3000002, Pos(9)), "Sender", "");

        Assert.Equal("Text:[|Position:Scene 9 · Line 3 (1203, 88, -455)|Text:]", Shape(h));
    }

    [Fact]
    public void LootRoll_FillsNoticeTemplateWithPlayerAndItemLinks()
    {
        var names = new FakeNames();
        names.Row(1043011, "{*player.name*} rolled the highest points and obtained {*item.name*}");
        names.Items[1013001] = ("Starfire Core", 4);

        var h = new ChatHypertextBuilder(names).Build(Raw(1043011, Player(777, "Mahiru"), Item(1013001)), "", "");

        Assert.False(h.IsShare);
        Assert.Equal(ChatLinkKind.Other, h.Kind);
        Assert.Equal("Player:Mahiru|Text: rolled the highest points and obtained |Item:Starfire Core", Shape(h));
        Assert.Equal(777L, h.Segments[0].PlayerId);
        Assert.Equal(1013001, h.Segments[2].ItemId);
        Assert.Equal(4, h.Segments[2].ItemQuality);
    }

    [Fact]
    public void RollPoints_DropsStyleTagsAndFillsVal()
    {
        var names = new FakeNames();
        names.Row(1043010, "<style=\"player_name\">{*player.name*}</style> rolled {*val*} points and obtained {*item.name*}");

        var h = new ChatHypertextBuilder(names).Build(Raw(1043010, Player(1, "A"), Val(87), Item(5)), "", "");

        Assert.Equal("Player:A|Text: rolled 87 points and obtained |Item:#5", Shape(h));
    }

    [Fact]
    public void ItemShare_EmptyContent_FallsBackToBracketedName()
    {
        var names = new FakeNames();
        names.Row(3000001, "");
        names.Items[42] = ("Gold Ore", 3);

        var h = new ChatHypertextBuilder(names).Build(Raw(3000001, Str("wts "), Item(42)), "", "");

        Assert.Equal(ChatLinkKind.Item, h.Kind);
        Assert.Equal("Text:wts [|Item:Gold Ore|Text:]", Shape(h));
        Assert.Equal(3, h.Segments[1].ItemQuality);
    }

    [Fact]
    public void HomeShare_NoLinkInTemplate_AppendsButtonLink()
    {
        var names = new FakeNames();
        names.Row(6000001, "Come over to my place and hang out!", button: "Visit My Home");
        var home = new ChatPlaceholder { Type = ChatPlaceholderType.Homeland, CharId = 99, HomelandId = 5, CommunityId = 6, Text = "Cozy" };

        var h = new ChatHypertextBuilder(names).Build(Raw(6000001, home), "Owner", "");

        Assert.Equal(ChatLinkKind.Home, h.Kind);
        Assert.Equal("Text:Come over to my place and hang out! [|Home:Visit My Home|Text:]", Shape(h));
        Assert.Equal(99L, h.Segments[1].PlayerId);
    }

    [Fact]
    public void PersonalSpace_UsesPlayerNamesSlotAsLinkText()
    {
        var names = new FakeNames();
        names.Row(3001001, "{*string[1]*}[ <u><link=1><style=chat_fishing_share_profile>{*player.names*}'s personal space</style></link></u> ]{*string[2]*}");

        var h = new ChatHypertextBuilder(names).Build(Raw(3001001, Player(5, "Yui")), "Yui", "");

        Assert.Equal(ChatLinkKind.Player, h.Kind);
        Assert.Equal("Text:[ |Player:Yui's personal space|Text: ]", Shape(h));
    }

    [Fact]
    public void UnknownRow_NoText_AutoJoinsSlots()
    {
        var h = new ChatHypertextBuilder(NullChatNameLookup.Instance).Build(Raw(123, Player(1, "A"), Val(3)), "", "");

        Assert.Equal("Player:A|Text: 3", Shape(h));
    }

    [Fact]
    public void UnknownRow_WithMsgText_RendersSanitisedText()
    {
        var h = new ChatHypertextBuilder(NullChatNameLookup.Instance).Build(Raw(1004020), "", "Guild <b>group</b> <sprite=3>");

        Assert.Equal("Text:Guild group <sprite=3>", Shape(h));
    }

    [Fact]
    public void Sanitize_MatchesGameRule()
    {
        Assert.Equal("ab<sprite=12>c", ChatRichText.Sanitize("a<size=300>b</size><sprite=1\n2>c\\"));
        Assert.Equal("<> a <3", ChatRichText.Sanitize("<> a <3"));          // "<>" and an unclosed '<' are text
        Assert.Equal(" x", ChatRichText.Sanitize("<3 <> x"));               // Lua "<[^>]+>" eats "<3 <>" too
        var same = "plain";
        Assert.Same(same, ChatRichText.Sanitize(same));
    }
}
