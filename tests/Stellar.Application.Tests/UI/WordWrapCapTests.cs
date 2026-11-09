using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// TextElement.MaxLines line breaking: words only, never mid-word. Width model = 10 px per character, tags and the
// one-char "…" counted like any other text except tags (zero width, like the real renderers).
public sealed class WordWrapCapTests
{
    private const string Ell = "…";

    private static float W(string s)
    {
        var n = 0; var inTag = false;
        foreach (var c in s)
        {
            if (c == '<') inTag = true;
            if (!inTag) n++;
            if (c == '>') inTag = false;
        }
        return n * 10f;
    }

    private static string Cap(string s, float maxW, int lines = 2) => WordWrapCap.Cap(s, W, maxW, lines, Ell);

    [Fact]
    public void Fitting_text_is_unchanged() => Assert.Equal("Maestro", Cap("Maestro", 100f));

    [Fact]
    public void Breaks_at_spaces() => Assert.Equal("Combat\nHistory", Cap("Combat History", 80f));

    [Fact]
    public void Overlong_single_word_is_cut_on_one_line_not_split()
        => Assert.Equal("CombatMe…", Cap("CombatMeter", 90f));   // was "CombatMete" / "r"

    [Fact]
    public void Overlong_word_is_cut_on_its_own_line_and_the_rest_continues()
        => Assert.Equal("Custom\nProfileI…", Cap("Custom ProfileImage", 90f));

    [Fact]
    public void Breaks_after_a_hyphen_keeping_the_hyphen()
        => Assert.Equal("Auto-\nGather", Cap("Auto-Gather", 70f));

    [Fact]
    public void Last_line_drops_words_before_the_ellipsis()
        => Assert.Equal("Custom\nProfile…", Cap("Custom Profile Image Manager", 120f));

    [Fact]
    public void Single_line_cap_drops_to_the_first_word()
        => Assert.Equal("Cooldown…", Cap("Cooldown Bar Plus", 110f, lines: 1));

    [Fact]
    public void Single_word_that_exactly_fits_needs_char_cut_for_ellipsis()
        => Assert.Equal("Coo…", Cap("Cooldown Bar", 40f, lines: 1));

    [Fact]
    public void Never_splits_a_tag_and_a_space_inside_a_tag_is_not_a_break()
        => Assert.Equal("<sprite name=a>Hi\nthere", Cap("<sprite name=a>Hi there", 50f));

    [Fact]
    public void Cjk_breaks_between_characters()
        => Assert.Equal("全体設\n定に…", Cap("全体設定に従う", 30f));

    [Fact]
    public void Nothing_fits_gives_ellipsis_or_empty()
    {
        Assert.Equal(Ell, Cap("Wide", 15f, lines: 1));
        Assert.Equal("", Cap("Wide", 5f, lines: 1));
    }

    [Fact]
    public void Collapses_repeated_spaces() => Assert.Equal("Dungeon\nHub", Cap("Dungeon   Hub", 80f));
}
