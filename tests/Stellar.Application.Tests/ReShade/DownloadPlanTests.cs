using System.Collections.Generic;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class DownloadPlanTests
{
    [Fact]
    public void ResolveTarget_combines_a_safe_relative_folder_under_the_data_folder()
    {
        var result = DownloadPlan.ResolveTarget("/data/photostudio", "reshade/packs/standard");
        Assert.Equal(System.IO.Path.Combine("/data/photostudio", "reshade/packs/standard"), result);
    }

    [Fact]
    public void ResolveTarget_null_when_empty() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", ""));

    [Fact]
    public void ResolveTarget_null_when_rooted() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", "/etc/passwd"));

    [Fact]
    public void ResolveTarget_null_when_parent_segment() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", "packs/../../escape"));

    [Fact]
    public void ResolveTarget_null_when_bare_parent_segment() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", ".."));

    [Fact]
    public void ResolveTarget_null_when_dot_segment() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", "packs/./standard"));

    [Fact]
    public void ResolveTarget_null_when_contains_backslash() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", "packs\\standard"));

    [Fact]
    public void ResolveTarget_null_when_contains_colon() =>
        Assert.Null(DownloadPlan.ResolveTarget("/data", "C:evil"));

    [Fact]
    public void MapZipEntry_null_for_directory_entry() =>
        Assert.Null(DownloadPlan.MapZipEntry("shaders/standard/", null));

    [Fact]
    public void MapZipEntry_null_for_rooted_entry() =>
        Assert.Null(DownloadPlan.MapZipEntry("/etc/passwd", null));

    [Fact]
    public void MapZipEntry_null_for_parent_segment_entry() =>
        Assert.Null(DownloadPlan.MapZipEntry("shaders/../../escape.fx", null));

    [Fact]
    public void MapZipEntry_null_for_dot_segment_entry() =>
        Assert.Null(DownloadPlan.MapZipEntry("shaders/./Bloom.fx", null));

    [Fact]
    public void MapZipEntry_null_for_colon_entry() =>
        Assert.Null(DownloadPlan.MapZipEntry("C:evil.fx", null));

    [Fact]
    public void MapZipEntry_normalizes_backslash_separators_to_forward_slash()
    {
        var result = DownloadPlan.MapZipEntry("shaders\\standard\\Bloom.fx", null);
        Assert.Equal("shaders/standard/Bloom.fx", result);
    }

    [Fact]
    public void MapZipEntry_null_prefixes_allows_every_file_entry()
    {
        Assert.Equal("shaders/Bloom.fx", DownloadPlan.MapZipEntry("shaders/Bloom.fx", null));
        Assert.Equal("textures/noise.png", DownloadPlan.MapZipEntry("textures/noise.png", null));
    }

    [Fact]
    public void MapZipEntry_null_when_outside_every_prefix()
    {
        var prefixes = new List<string> { "shaders" };
        Assert.Null(DownloadPlan.MapZipEntry("textures/noise.png", prefixes));
    }

    [Fact]
    public void MapZipEntry_allows_entry_inside_a_prefix()
    {
        var prefixes = new List<string> { "shaders", "textures" };
        Assert.Equal("shaders/Bloom.fx", DownloadPlan.MapZipEntry("shaders/Bloom.fx", prefixes));
        Assert.Equal("textures/noise.png", DownloadPlan.MapZipEntry("textures/noise.png", prefixes));
    }

    [Fact]
    public void MapZipEntry_prefix_match_is_segment_bounded_not_a_bare_substring()
    {
        // "shaders-extra/Bloom.fx" must NOT match prefix "shaders" (substring but not a path segment).
        var prefixes = new List<string> { "shaders" };
        Assert.Null(DownloadPlan.MapZipEntry("shaders-extra/Bloom.fx", prefixes));
    }
}
