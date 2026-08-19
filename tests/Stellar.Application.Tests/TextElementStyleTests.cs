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
}
