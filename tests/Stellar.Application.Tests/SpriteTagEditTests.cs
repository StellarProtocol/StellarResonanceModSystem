using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests;

/// <summary>
/// Pins atomic <c>&lt;sprite=N&gt;</c> editing for the rich-emoji input (InputElement.RichEmoji): an edit that
/// removes PART of a tag removes the whole tag (caret after the edit); an edit that already removed whole tags,
/// or never touched one, is left alone.
/// </summary>
public class SpriteTagEditTests
{
    [Theory]
    // Backspace that removed only the closing '>' / a digit → whole tag goes, caret where it started.
    [InlineData("hi<sprite=12>", "hi<sprite=12", "hi", 2)]
    [InlineData("hi<sprite=12>x", "hi<sprite=1>x", "hix", 2)]
    // Forward Delete that removed only the '<'.
    [InlineData("a<sprite=3>b", "asprite=3>b", "ab", 1)]
    // Adjacent identical tags: the touched one goes, the other survives.
    [InlineData("<sprite=1><sprite=1>", "<sprite=1><sprite=1", "<sprite=1>", 10)]
    [InlineData("<sprite=1><sprite=1>", "<sprite=1<sprite=1>", "<sprite=1>", 0)]
    // Selection replace that cut into a tag keeps the typed text.
    [InlineData("ab<sprite=5>cd", "aXte=5>cd", "aXcd", 2)]
    public void Partial_tag_edit_is_widened_to_the_whole_tag(string before, string after, string expect, int caret)
    {
        Assert.True(SpriteTagEdit.TryRepair(before, after, out var repaired, out var c));
        Assert.Equal(expect, repaired);
        Assert.Equal(caret, c);
    }

    [Theory]
    [InlineData("hi<sprite=12>", "hi")]                 // TMP already removed the whole tag
    [InlineData("hi<sprite=12>", "h<sprite=12>")]       // edit outside the tag
    [InlineData("hi<sprite=12>", "hi<sprite=12>!")]     // pure insertion
    [InlineData("plain text", "plain tex")]             // no tag at all
    [InlineData("", "a")]
    public void Edit_that_does_not_split_a_tag_is_left_alone(string before, string after)
        => Assert.False(SpriteTagEdit.TryRepair(before, after, out _, out _));
}
