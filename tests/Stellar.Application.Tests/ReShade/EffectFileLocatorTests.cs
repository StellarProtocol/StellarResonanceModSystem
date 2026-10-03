using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class EffectFileLocatorTests
{
    private readonly InMemoryEffectFiles _fs = new();

    private EffectFileLocator Locator(params string[] paths)
    {
        var locator = new EffectFileLocator(_fs);
        locator.SetSearchPaths(paths);
        return locator;
    }

    [Theory]
    [InlineData("/a/b/**", "/a/b", true)]
    [InlineData("/a/b/", "/a/b", false)]
    [InlineData("/a/b", "/a/b", false)]
    [InlineData(" \"/a/b/**\" ", "/a/b", true)]
    [InlineData("C:\\fx\\**", "C:\\fx", true)]
    public void Search_paths_ending_in_double_star_are_recursive(string raw, string root, bool recursive)
    {
        var path = ReShadeSearchPath.Parse(raw);
        Assert.Equal(root, path.Root);
        Assert.Equal(recursive, path.Recursive);
    }

    [Fact]
    public void A_plain_search_path_only_looks_in_its_own_folder()
    {
        _fs.Add("/a/sub/X.fx", "x");
        Assert.Null(Locator("/a").FindEffect("X.fx"));
        _fs.Add("/a/X.fx", "x");
        Assert.Equal("/a/X.fx", Locator("/a").FindEffect("X.fx"));
    }

    [Fact]
    public void A_recursive_search_path_looks_in_every_subfolder()
    {
        _fs.Add("/a/sub/deep/X.fx", "x");
        Assert.Equal("/a/sub/deep/X.fx", Locator("/a/**").FindEffect("X.fx"));
    }

    [Fact]
    public void The_first_search_path_that_holds_the_file_wins()
    {
        _fs.Add("/one/X.fx", "1");
        _fs.Add("/two/X.fx", "2");
        Assert.Equal("/two/X.fx", Locator("/missing/**", "/two/**", "/one/**").FindEffect("X.fx"));
    }

    [Fact]
    public void An_include_resolves_next_to_its_file_first_then_on_the_search_paths()
    {
        _fs.Add("/pack/Shaders/A.fx", "a");
        _fs.Add("/pack/Shaders/Local.fxh", "l");
        _fs.Add("/pack/Shaders/lib/Shared.fxh", "s");
        var locator = Locator("/pack/**");
        Assert.Equal("/pack/Shaders/Local.fxh", locator.ResolveInclude("/pack/Shaders/A.fx", "Local.fxh"));
        Assert.Equal("/pack/Shaders/lib/Shared.fxh", locator.ResolveInclude("/pack/Shaders/A.fx", "Shared.fxh"));
        Assert.Null(locator.ResolveInclude("/pack/Shaders/A.fx", "Nowhere.fxh"));
    }

    [Fact]
    public void Subfolders_are_listed_once_until_the_cache_is_cleared()
    {
        _fs.Add("/a/sub/X.fx", "x");
        var locator = Locator("/a/**");
        locator.FindEffect("X.fx");
        locator.FindEffect("Y.fx");
        Assert.Equal(1, _fs.DirectoryScans);
        locator.ClearCache();
        locator.FindEffect("X.fx");
        Assert.Equal(2, _fs.DirectoryScans);
    }

    [Fact]
    public void No_search_paths_finds_nothing()
    {
        _fs.Add("/a/X.fx", "x");
        Assert.Null(Locator().FindEffect("X.fx"));
    }
}
