using Stellar.Abstractions.Services;
using Xunit;

namespace Stellar.Application.Tests;

/// <summary>Pins the TextElement typography flags (i18n typography stage B): all default OFF so every
/// existing element keeps the legacy path, and each is init-settable for plugins.</summary>
public class TextElementStyleTests
{
    [Fact]
    public void Style_flags_default_off_and_are_init_settable()
    {
        var plain = new TextElement(() => "x");
        Assert.False(plain.Bold);
        Assert.False(plain.Italic);
        Assert.False(plain.Underline);
        Assert.False(plain.Strikethrough);

        var styled = new TextElement(() => "x") { Bold = true, Italic = true, Underline = true, Strikethrough = true };
        Assert.True(styled.Bold);
        Assert.True(styled.Italic);
        Assert.True(styled.Underline);
        Assert.True(styled.Strikethrough);
    }

    // 2.30.0 MaxLines: an init property (binary-compatible — no new positional ctor parameter), default 0 = unlimited.
    [Fact]
    public void MaxLines_defaults_to_unlimited_and_is_init_settable_without_a_new_ctor_parameter()
    {
        Assert.Equal(0, new TextElement(() => "x").MaxLines);
        Assert.Equal(2, new TextElement(() => "x") { MaxLines = 2 }.MaxLines);
        var primary = typeof(TextElement).GetConstructors();
        Assert.DoesNotContain(primary, c => System.Array.Exists(c.GetParameters(), p => p.Name == "MaxLines"));
    }
}
