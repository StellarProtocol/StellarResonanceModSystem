using System.IO;
using System.Linq;
using System.Text.Json;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Localization;

public sealed class UiLanguagesTests
{
    [Fact]
    public void Codes_are_the_six_ui_languages_in_dropdown_order()
        => Assert.Equal(new[] { "en", "ja", "th", "id", "fil", "ko" }, UiLanguages.Codes);

    [Fact]
    public void Native_names_align_with_codes()
        => Assert.Equal(new[] { "English", "日本語", "ไทย", "Bahasa Indonesia", "Filipino", "한국어" }, UiLanguages.NativeNames);

    // Pins the Filipino-class bug: a code in the list without a complete shipped catalog.
    [Fact]
    public void Every_code_ships_a_catalog_with_the_english_key_set()
    {
        var langDir = Path.Combine(RepoRoot(), "src", "Stellar.Infrastructure", "Lang");
        var en = Keys(Path.Combine(langDir, "en.json"));
        foreach (var code in UiLanguages.Codes)
        {
            var path = Path.Combine(langDir, code + ".json");
            Assert.True(File.Exists(path), $"missing Lang/{code}.json");
            Assert.Equal(en, Keys(path));
        }
    }

    [Theory]
    [MemberData(nameof(AllCodes))]
    public void Engine_accepts_every_listed_code(string code)
    {
        var e = new LocalizationEngine(new FakeConfigSection(), new FakeProbe(), new FakeLog());
        e.SetLanguageSetting(code);
        Assert.Equal(code, e.ActiveLanguage);
    }

    public static System.Collections.Generic.IEnumerable<object[]> AllCodes()
        => UiLanguages.Codes.Select(c => new object[] { c });

    private static string[] Keys(string path)
        => JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(path))!
            .Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray();

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Stellar.Infrastructure"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("framework root not found");
    }
}
