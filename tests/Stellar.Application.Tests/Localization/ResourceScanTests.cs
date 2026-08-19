using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Localization;

public sealed class ResourceScanTests
{
    [Theory]
    [InlineData("Stellar.Infrastructure.Lang.en.json", "en")]
    [InlineData("Lang.ja.json", "ja")]
    [InlineData("Stellar.CombatMeter.Lang.th.json", "th")]
    [InlineData("Whatever.Lang.id.json", "id")]
    [InlineData("Stellar.Infrastructure.icons.gear.png", null)]
    [InlineData("Lang.ko.json", null)]   // unsupported code ignored
    public void Matches_supported_lang_resources(string name, string? code)
        => Assert.Equal(code, LocalizationResourceScan.CodeFromResourceName(name));
}
