using System.Collections.Generic;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class EffectFileLocatorTests
{
    private readonly InMemoryEffectFiles _fs = new();

    // The single answer, null when none; an ambiguous result fails the test (these cases expect exactly one).
    private static string? Only(IReadOnlyList<string> candidates) => candidates.Count == 0 ? null : Assert.Single(candidates);

    private EffectFileLocator Locator(params string[] paths) => LocatorWithCap(EffectFileLocator.MaxFilesPerRoot, paths);

    private EffectFileLocator LocatorWithCap(int cap, params string[] paths)
    {
        var locator = new EffectFileLocator(_fs, cap);
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
        Assert.Null(Only(Locator("/a").FindEffectCandidates("X.fx")));
        _fs.Add("/a/X.fx", "x");
        Assert.Equal("/a/X.fx", Only(Locator("/a").FindEffectCandidates("X.fx")));
    }

    [Fact]
    public void A_recursive_search_path_looks_in_every_subfolder()
    {
        _fs.Add("/a/sub/deep/X.fx", "x");
        Assert.Equal("/a/sub/deep/X.fx", Only(Locator("/a/**").FindEffectCandidates("X.fx")));
    }

    [Fact]
    public void The_first_search_path_that_holds_the_file_wins()
    {
        _fs.Add("/one/X.fx", "1");
        _fs.Add("/two/X.fx", "2");
        Assert.Equal("/two/X.fx", Only(Locator("/missing/**", "/two/**", "/one/**").FindEffectCandidates("X.fx")));
    }

    [Fact]
    public void An_include_resolves_next_to_its_file_first_then_on_the_search_paths()
    {
        _fs.Add("/pack/Shaders/A.fx", "a");
        _fs.Add("/pack/Shaders/Local.fxh", "l");
        _fs.Add("/pack/Shaders/lib/Shared.fxh", "s");
        var locator = Locator("/pack/**");
        Assert.Equal("/pack/Shaders/Local.fxh", Only(locator.ResolveIncludeCandidates("/pack/Shaders/A.fx", "Local.fxh")));
        Assert.Equal("/pack/Shaders/lib/Shared.fxh", Only(locator.ResolveIncludeCandidates("/pack/Shaders/A.fx", "Shared.fxh")));
        Assert.Null(Only(locator.ResolveIncludeCandidates("/pack/Shaders/A.fx", "Nowhere.fxh")));
    }

    [Fact]
    public void Each_root_is_walked_once_across_many_lookups()
    {
        _fs.Add("/a/sub/X.fx", "x");
        _fs.Add("/b/Y.fx", "y");
        _fs.Add("/b/Shared.fxh", "s");
        var locator = Locator("/a/**", "/b");
        for (var i = 0; i < 20; i++)
        {
            Only(locator.FindEffectCandidates("X.fx"));
            Only(locator.FindEffectCandidates("Y.fx"));
            Only(locator.FindEffectCandidates("Missing.fx"));
            Only(locator.ResolveIncludeCandidates("/a/sub/X.fx", "Shared.fxh"));
        }
        Assert.Equal(1, _fs.WalksOf("/a"));
        Assert.Equal(1, _fs.WalksOf("/b"));
    }

    [Fact]
    public void The_file_index_is_rebuilt_after_the_cache_is_cleared()
    {
        var locator = Locator("/a/**");
        Assert.Null(Only(locator.FindEffectCandidates("Late.fx")));
        _fs.Add("/a/sub/Late.fx", "x");
        Assert.Null(Only(locator.FindEffectCandidates("Late.fx")));
        locator.ClearCache();
        Assert.Equal("/a/sub/Late.fx", Only(locator.FindEffectCandidates("Late.fx")));
        Assert.Equal(2, _fs.WalksOf("/a"));
    }

    [Fact]
    public void Lookups_ignore_letter_case()
    {
        _fs.Add("/a/sub/Clarity.fx", "x");
        Assert.Equal("/a/sub/Clarity.fx", Only(Locator("/a/**").FindEffectCandidates("CLARITY.fx")));
    }

    [Fact]
    public void A_file_directly_in_a_recursive_root_wins_over_one_in_a_subfolder()
    {
        _fs.Add("/a/A/X.fx", "sub");
        _fs.Add("/a/X.fx", "direct");
        Assert.Equal("/a/X.fx", Only(Locator("/a/**").FindEffectCandidates("X.fx")));
    }

    [Fact]
    public void An_include_with_a_folder_part_matches_the_end_of_an_indexed_path()
    {
        _fs.Add("/pack/Shaders/A.fx", "a");
        _fs.Add("/pack/Shaders/other/Shared.fxh", "wrong folder");
        _fs.Add("/pack/Shaders/lib/Shared.fxh", "s");
        Assert.Equal("/pack/Shaders/lib/Shared.fxh", Only(Locator("/pack/**").ResolveIncludeCandidates("/elsewhere/A.fx", "lib/Shared.fxh")));
    }

    [Fact]
    public void Files_past_the_per_root_cap_are_not_found()
    {
        _fs.Add("/a/1.fx", "1");
        _fs.Add("/a/2.fx", "2");
        _fs.Add("/a/3.fx", "3");
        var locator = LocatorWithCap(2, "/a/**");
        Assert.Equal("/a/1.fx", Only(locator.FindEffectCandidates("1.fx")));
        Assert.Null(Only(locator.FindEffectCandidates("3.fx")));
    }

    [Fact]
    public void A_rooted_include_is_refused()
    {
        _fs.Add("/pack/Shaders/Local.fxh", "l");
        Assert.Null(Only(Locator("/pack/**").ResolveIncludeCandidates("/pack/Shaders/A.fx", "/pack/Shaders/Local.fxh")));
    }

    [Fact]
    public void A_relative_include_resolves_to_a_normalized_full_path()
    {
        _fs.Add("/pack/Shaders/Local.fxh", "l");
        Assert.Equal("/pack/Shaders/Local.fxh", Only(Locator().ResolveIncludeCandidates("/pack/Shaders/A.fx", "../Shaders/Local.fxh")));
    }

    [Fact]
    public void No_search_paths_finds_nothing()
    {
        _fs.Add("/a/X.fx", "x");
        Assert.Null(Only(Locator().FindEffectCandidates("X.fx")));
    }

    [Fact]
    public void Ambiguous_suffix_matches_in_one_root_are_all_returned_in_enumeration_order()
    {
        _fs.Add("/root/packB/Common.fxh", "b");
        _fs.Add("/root/packA/Common.fxh", "a");
        var candidates = Locator("/root/**").ResolveIncludeCandidates("/elsewhere/X.fx", "Common.fxh");
        Assert.Equal(new[] { "/root/packA/Common.fxh", "/root/packB/Common.fxh" }, candidates);
    }

    [Fact]
    public void A_direct_match_is_the_only_candidate()
    {
        _fs.Add("/root/Common.fxh", "root");
        _fs.Add("/root/packA/Common.fxh", "a");
        Assert.Equal(new[] { "/root/Common.fxh" }, Locator("/root/**").FindEffectCandidates("Common.fxh"));
    }

    [Fact]
    public void A_local_include_is_the_only_candidate_and_a_rooted_one_has_none()
    {
        _fs.Add("/pack/Shaders/Local.fxh", "l");
        _fs.Add("/pack/Other/Local.fxh", "o");
        var locator = Locator("/pack/**");
        Assert.Equal(new[] { "/pack/Shaders/Local.fxh" }, locator.ResolveIncludeCandidates("/pack/Shaders/A.fx", "Local.fxh"));
        Assert.Empty(locator.ResolveIncludeCandidates("/pack/Shaders/A.fx", "/pack/Shaders/Local.fxh"));
    }
}
