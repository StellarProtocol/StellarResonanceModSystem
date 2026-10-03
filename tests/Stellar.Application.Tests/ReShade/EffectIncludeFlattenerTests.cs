using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Application.Services;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class EffectIncludeFlattenerTests
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    private string? Read(string path) => _files.TryGetValue(path, out var text) ? text : null;

    // Same folder as the including file, then "/shared".
    private string? Resolve(string includingFile, string name)
    {
        var local = Path.Combine(Path.GetDirectoryName(includingFile)!, name);
        if (_files.ContainsKey(local)) return local;
        var shared = Path.Combine("/shared", name);
        return _files.ContainsKey(shared) ? shared : null;
    }

    private IReadOnlyList<string> ResolveAll(string includingFile, string name) =>
        Resolve(includingFile, name) is { } path ? new[] { path } : Array.Empty<string>();

    private string? Flatten(string root) => EffectIncludeFlattener.Flatten(root, Read, ResolveAll);

    private const string StandardHeader =
        "namespace ReShade { texture DepthBufferTex : DEPTH; sampler DepthBuffer { Texture = DepthBufferTex; };\n" +
        "float GetLinearizedDepth(float2 uv) { return tex2Dlod(DepthBuffer, float4(uv, 0, 0)).x; } }";

    [Fact]
    public void Source_without_includes_comes_back_unchanged()
    {
        _files["/fx/A.fx"] = "float4 main() { return 1; }";
        Assert.Equal("float4 main() { return 1; }", Flatten("/fx/A.fx"));
    }

    [Fact]
    public void Quoted_and_angle_includes_are_inlined_in_place()
    {
        _files["/fx/A.fx"] = "#include \"Local.fxh\"\n  #  include <Shared.fxh>\nbody";
        _files["/fx/Local.fxh"] = "LOCAL";
        _files["/shared/Shared.fxh"] = "SHARED";
        var flat = Flatten("/fx/A.fx")!;
        Assert.Contains("LOCAL", flat);
        Assert.Contains("SHARED", flat);
        Assert.Contains("body", flat);
        Assert.DoesNotContain("#include", flat);
    }

    [Fact]
    public void The_including_files_own_folder_wins_over_the_search_paths()
    {
        _files["/fx/A.fx"] = "#include \"X.fxh\"";
        _files["/fx/X.fxh"] = "LOCAL";
        _files["/shared/X.fxh"] = "SHARED";
        Assert.Equal("LOCAL", Flatten("/fx/A.fx")!.Trim());
    }

    [Fact]
    public void Nested_includes_are_flattened_recursively()
    {
        _files["/fx/A.fx"] = "#include \"B.fxh\"";
        _files["/fx/B.fxh"] = "#include \"C.fxh\"";
        _files["/shared/C.fxh"] = "float d = ReShade::GetLinearizedDepth(uv);";
        Assert.True(EffectDepthScanner.UsesDepth(Flatten("/fx/A.fx")));
    }

    [Theory]
    [InlineData("ReShade.fxh")]
    [InlineData("RESHADE.FXH")]
    [InlineData("ReShadeUI.fxh")]
    [InlineData("reshadeui.fxh")]
    public void ReShades_standard_headers_are_skipped_in_any_case(string header)
    {
        _files["/fx/Bloom.fx"] = $"#include \"{header}\"\nfloat4 PS() {{ return tex2D(ReShade::BackBuffer, uv); }}";
        _files[$"/shared/{header}"] = StandardHeader;
        var flat = Flatten("/fx/Bloom.fx");
        Assert.NotNull(flat);
        Assert.DoesNotContain("DepthBufferTex", flat);
        Assert.False(EffectDepthScanner.UsesDepth(flat));
    }

    [Fact]
    public void A_skipped_standard_header_need_not_exist()
    {
        _files["/fx/Bloom.fx"] = "#include \"ReShade.fxh\"\nbody";
        Assert.Contains("body", Flatten("/fx/Bloom.fx"));
    }

    [Fact]
    public void An_effect_calling_GetLinearizedDepth_itself_still_uses_depth_with_the_header_skipped()
    {
        _files["/fx/MXAO.fx"] = "#include \"ReShade.fxh\"\nfloat d = ReShade::GetLinearizedDepth(uv);";
        _files["/shared/ReShade.fxh"] = StandardHeader;
        Assert.True(EffectDepthScanner.UsesDepth(Flatten("/fx/MXAO.fx")));
    }

    [Fact]
    public void A_pack_helper_header_that_reads_depth_is_still_flattened()
    {
        _files["/fx/Fog.fx"] = "#include \"ReShade.fxh\"\n#include \"PackHelpers.fxh\"\nfloat4 PS() { return Fog(uv); }";
        _files["/shared/ReShade.fxh"] = StandardHeader;
        _files["/shared/PackHelpers.fxh"] = "#include \"ReShade.fxh\"\nfloat4 Fog(float2 uv) { return ReShade::GetLinearizedDepth(uv); }";
        Assert.True(EffectDepthScanner.UsesDepth(Flatten("/fx/Fog.fx")));
    }

    [Fact]
    public void A_cycle_terminates_and_keeps_each_file_once()
    {
        _files["/fx/A.fx"] = "#include \"B.fxh\"\nA_BODY";
        _files["/fx/B.fxh"] = "#include \"A.fx\"\nB_BODY";
        var flat = Flatten("/fx/A.fx")!;
        Assert.Contains("A_BODY", flat);
        Assert.Contains("B_BODY", flat);
        Assert.Equal(flat.IndexOf("A_BODY", StringComparison.Ordinal), flat.LastIndexOf("A_BODY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_included_twice_is_inlined_once()
    {
        _files["/fx/A.fx"] = "#include \"S.fxh\"\n#include \"S.fxh\"";
        _files["/fx/S.fxh"] = "SHARED_ONCE";
        var flat = Flatten("/fx/A.fx")!;
        Assert.Equal(flat.IndexOf("SHARED_ONCE", StringComparison.Ordinal), flat.LastIndexOf("SHARED_ONCE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chain_deeper_than_the_cap_is_unreadable()
    {
        _files["/fx/A.fx"] = "#include \"I0.fxh\"";
        for (var i = 0; i <= EffectIncludeFlattener.MaxDepth; i++)
            _files[$"/fx/I{i}.fxh"] = $"#include \"I{i + 1}.fxh\"";
        _files[$"/fx/I{EffectIncludeFlattener.MaxDepth + 1}.fxh"] = "END";
        Assert.Null(Flatten("/fx/A.fx"));
    }

    [Fact]
    public void A_chain_at_the_cap_still_flattens()
    {
        _files["/fx/A.fx"] = "#include \"I1.fxh\"";
        for (var i = 1; i < EffectIncludeFlattener.MaxDepth; i++)
            _files[$"/fx/I{i}.fxh"] = $"#include \"I{i + 1}.fxh\"";
        _files[$"/fx/I{EffectIncludeFlattener.MaxDepth}.fxh"] = "END";
        Assert.Contains("END", Flatten("/fx/A.fx"));
    }

    [Fact]
    public void A_missing_include_makes_the_whole_source_unreadable()
    {
        _files["/fx/A.fx"] = "#include \"Gone.fxh\"\nbody";
        Assert.Null(Flatten("/fx/A.fx"));
    }

    [Fact]
    public void An_unreadable_root_is_null()
    {
        Assert.Null(Flatten("/fx/Missing.fx"));
    }

    [Fact]
    public void A_commented_out_include_is_left_alone()
    {
        _files["/fx/A.fx"] = "// #include \"Gone.fxh\"\nbody";
        Assert.Equal("// #include \"Gone.fxh\"\nbody", Flatten("/fx/A.fx"));
    }
}
