using System;
using System.IO;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class ReShadeServiceTests
{
    private readonly FakeReShadeNative _native = new();
    private readonly InMemoryEffectFiles _fs = new();
    private readonly StubLog _log = new();
    private readonly ReShadeService _service;
    private int _changed;

    public ReShadeServiceTests()
    {
        _service = new ReShadeService(_native, new EffectDepthIndex(_fs), new EffectSizeLockIndex(_fs), new EffectTemporalIndex(_fs), _log);
        _service.Changed += () => _changed++;
    }

    private void Tick()
    {
        _native.NextFrame();
        _service.Refresh();
    }

    [Fact]
    public void Not_loaded_is_unavailable_and_quiet()
    {
        _native.IsLoaded = false;
        _service.Refresh();
        Assert.Equal(ReShadeState.NotInstalled, _service.State);
        Assert.Empty(_service.Techniques);
        Assert.Null(_service.CurrentPreset);
        Assert.False(_service.Enabled);
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void First_refresh_lists_techniques_and_raises_Changed_once()
    {
        _native.Add("Clarity", "Clarity.fx");
        _native.Add("MXAO", "MXAO.fx", enabled: false);
        _native.Preset = "C:\\game\\ReShadePreset.ini";
        Tick();
        Assert.Equal(ReShadeState.Ready, _service.State);
        Assert.Equal(2, _service.Techniques.Count);
        Assert.Equal("Clarity", _service.Techniques[0].Name);
        Assert.Equal("Clarity.fx", _service.Techniques[0].EffectFile);
        Assert.True(_service.Techniques[0].Enabled);
        Assert.False(_service.Techniques[1].Enabled);
        Assert.Equal("C:\\game\\ReShadePreset.ini", _service.CurrentPreset);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void Nothing_is_read_while_the_snapshot_frame_counter_stands_still()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        var reads = _native.TechniqueReads;
        _service.Refresh();
        _service.Refresh();
        Assert.Equal(reads, _native.TechniqueReads);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void Moving_frames_without_a_difference_raise_nothing()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        for (var i = 0; i < ReShadeService.ReReadEveryTicks * 3; i++)
            Tick();
        Assert.True(_native.TechniqueReads > 1);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public void A_toggle_made_outside_Stellar_is_picked_up_within_the_reread_interval()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        _native.Techniques[0] = ("Clarity", "Clarity.fx", false);
        for (var i = 0; i < ReShadeService.ReReadEveryTicks; i++)
            Tick();
        Assert.False(_service.Techniques[0].Enabled);
        Assert.Equal(2, _changed);
    }

    [Fact]
    public void A_technique_count_change_is_read_on_the_next_tick()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        _native.Add("Vibrance", "Vibrance.fx");
        Tick();
        Assert.Equal(2, _service.Techniques.Count);
        Assert.Equal(2, _changed);
    }

    [Fact]
    public void Loading_makes_it_unavailable_with_no_techniques()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        _native.Loading = true;
        Tick();
        Assert.Equal(ReShadeState.Loading, _service.State);
        Assert.Empty(_service.Techniques);
        Assert.Equal(2, _changed);
        _native.Loading = false;
        Tick();
        Assert.Equal(ReShadeState.Ready, _service.State);
        Assert.Single(_service.Techniques);
        Assert.Equal(3, _changed);
    }

    [Fact]
    public void Not_ready_is_unavailable()
    {
        _native.Ready = false;
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Assert.Equal(ReShadeState.Loading, _service.State);
        Assert.Empty(_service.Techniques);
    }

    [Fact]
    public void Losing_the_add_on_clears_everything_and_raises_Changed()
    {
        _native.Add("Clarity", "Clarity.fx");
        _native.Preset = "p.ini";
        Tick();
        _native.IsLoaded = false;
        _service.Refresh();
        Assert.Equal(ReShadeState.NotInstalled, _service.State);
        Assert.Empty(_service.Techniques);
        Assert.Null(_service.CurrentPreset);
        Assert.Equal(2, _changed);
    }

    [Fact]
    public void A_preset_switch_seen_in_the_snapshot_raises_Changed()
    {
        _native.Add("Clarity", "Clarity.fx");
        _native.Preset = "a.ini";
        Tick();
        _native.Preset = "b.ini";
        for (var i = 0; i < ReShadeService.ReReadEveryTicks; i++)
            Tick();
        Assert.Equal("b.ini", _service.CurrentPreset);
        Assert.Equal(2, _changed);
    }

    [Fact]
    public void SetTechnique_requests_a_saved_change_for_that_effect_and_name_and_rereads_next_tick()
    {
        _native.Add("Clarity", "Clarity.fx");
        _native.Add("Clarity", "OtherPack.fx");
        Tick();
        _service.SetTechnique("Clarity.fx", "Clarity", false);
        Assert.Equal(new[] { "technique Clarity.fx/Clarity on=False save=True" }, _native.Requests);
        _native.Techniques[0] = ("Clarity", "Clarity.fx", false);
        Tick();
        Assert.False(_service.Techniques[0].Enabled);
    }

    [Fact]
    public void SetTechnique_ignores_an_empty_name_or_effect_and_a_missing_add_on()
    {
        _service.SetTechnique("Clarity.fx", "", true);
        _service.SetTechnique("", "Clarity", true);
        _native.IsLoaded = false;
        _service.SetTechnique("Clarity.fx", "Clarity", true);
        Assert.Empty(_native.Requests);
    }

    [Fact]
    public void Enabled_reads_the_snapshot_and_requests_the_change()
    {
        _native.EffectsEnabled = true;
        Assert.True(_service.Enabled);
        _service.Enabled = false;
        Assert.Equal(new[] { "enabled False" }, _native.Requests);
    }

    [Fact]
    public void SetPreset_goes_straight_through_when_techniques_are_listed()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        _service.SetPreset("C:\\p\\Night.ini");
        Assert.Equal(new[] { "preset C:\\p\\Night.ini" }, _native.Requests);
    }

    [Fact]
    public void SetPreset_is_held_while_no_technique_is_listed_and_sent_once_they_are()
    {
        Tick();
        _service.SetPreset("C:\\p\\Night.ini");
        Tick();
        Assert.Empty(_native.Requests);
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Tick();
        Assert.Equal(new[] { "preset C:\\p\\Night.ini" }, _native.Requests);
    }

    [Fact]
    public void A_held_preset_is_replaced_by_a_later_one()
    {
        _service.SetPreset("first.ini");
        _service.SetPreset("second.ini");
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Assert.Equal(new[] { "preset second.ini" }, _native.Requests);
    }

    [Fact]
    public void A_held_preset_waits_out_a_load()
    {
        _service.SetPreset("p.ini");
        _native.Add("Clarity", "Clarity.fx");
        _native.Loading = true;
        Tick();
        Assert.Empty(_native.Requests);
        _native.Loading = false;
        Tick();
        Assert.Equal(new[] { "preset p.ini" }, _native.Requests);
    }

    [Fact]
    public void SetPreset_before_the_add_on_binds_is_held_until_it_lists_techniques()
    {
        _native.IsLoaded = false;
        _service.SetPreset("p.ini");
        _service.Refresh();
        _native.IsLoaded = true;
        Tick();
        Assert.Empty(_native.Requests);
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Assert.Equal(new[] { "preset p.ini" }, _native.Requests);
    }

    [Fact]
    public void SetSearchPaths_before_the_add_on_binds_is_sent_once_it_does()
    {
        _native.IsLoaded = false;
        _service.SetSearchPaths(new[] { "/old" }, Array.Empty<string>());
        _service.SetSearchPaths(new[] { "/new" }, Array.Empty<string>());
        _service.Refresh();
        Assert.Empty(_native.Requests);
        _native.IsLoaded = true;
        Tick();
        Tick();
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(new[] { $"paths effects=/new{sep}** textures=<unchanged>" }, _native.Requests);
    }

    [Fact]
    public void SetSearchPaths_adds_recursion_and_joins_with_semicolons()
    {
        _service.SetSearchPaths(new[] { "/data/pack/Shaders/", "/data/more" }, new[] { "/data/pack/Textures" });
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(new[]
        {
            $"paths effects=/data/pack/Shaders{sep}**;/data/more{sep}** textures=/data/pack/Textures{sep}**",
        }, _native.Requests);
    }

    [Fact]
    public void SetSearchPaths_skips_relative_and_semicolon_folders_with_a_warning()
    {
        _service.SetSearchPaths(new[] { "relative/dir", "/a;b", "/ok" }, Array.Empty<string>());
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(new[] { $"paths effects=/ok{sep}** textures=<unchanged>" }, _native.Requests);
        Assert.Equal(2, _log.WarningLines.Count);
    }

    [Fact]
    public void A_bad_search_path_is_warned_about_once()
    {
        for (var i = 0; i < 3; i++)
            _service.SetSearchPaths(new[] { "relative/dir", "/ok" }, Array.Empty<string>());
        _service.SetSearchPaths(new[] { "other/relative" }, Array.Empty<string>());
        Assert.Equal(2, _log.WarningLines.Count);
    }

    [Fact]
    public void SetSearchPaths_with_nothing_usable_sends_nothing()
    {
        _service.SetSearchPaths(Array.Empty<string>(), Array.Empty<string>());
        Assert.Empty(_native.Requests);
    }

    [Fact]
    public void UsesDepth_comes_from_the_effect_source_found_on_the_search_paths()
    {
        _fs.Add("/pack/Shaders/Clarity.fx", "float4 PS() { return tex2D(ReShade::BackBuffer, uv); }");
        _fs.Add("/pack/Shaders/MXAO.fx", "#include \"Depth.fxh\"");
        _fs.Add("/pack/Shaders/Depth.fxh", "float d = ReShade::GetLinearizedDepth(uv);");
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        _native.Add("Clarity", "Clarity.fx");
        _native.Add("MXAO", "MXAO.fx");
        _native.Add("Unknown", "NotOnDisk.fx");
        Tick();
        Assert.False(_service.Techniques[0].UsesDepth);
        Assert.True(_service.Techniques[1].UsesDepth);
        Assert.True(_service.Techniques[2].UsesDepth);
    }

    [Fact]
    public void SizeLocked_comes_from_the_effect_source_found_on_the_search_paths()
    {
        _fs.Add("/pack/Shaders/Clarity.fx", "float4 PS() { return tex2D(ReShade::BackBuffer, uv); }");
        _fs.Add("/pack/Shaders/Bloom.fx", "#include \"BloomLib.fxh\"");
        _fs.Add("/pack/Shaders/BloomLib.fxh", "texture BloomTex { Width = BUFFER_WIDTH / 2; Height = BUFFER_HEIGHT / 2; };");
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        _native.Add("Clarity", "Clarity.fx");
        _native.Add("Bloom", "Bloom.fx");
        _native.Add("Unknown", "NotOnDisk.fx");
        Tick();
        Assert.False(_service.Techniques[0].SizeLocked);
        Assert.True(_service.Techniques[1].SizeLocked);
        Assert.True(_service.Techniques[2].SizeLocked);   // not found -> assume locked
    }

    [Fact]
    public void IsTemporal_comes_from_the_effect_source_and_unknown_counts_as_temporal()
    {
        _fs.Add("/pack/Shaders/Clarity.fx", "float4 PS() { return tex2D(ReShade::BackBuffer, uv); }");
        _fs.Add("/pack/Shaders/Bloom.fx", "uniform float frametime < source = \"frametime\"; >;");
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        _native.Add("Clarity", "Clarity.fx");
        _native.Add("Bloom", "Bloom.fx");
        Assert.True(_service.IsTemporal("Clarity.fx"));   // not resolved yet
        Tick();
        Assert.False(_service.IsTemporal("Clarity.fx"));
        Assert.True(_service.IsTemporal("Bloom.fx"));
        Assert.True(_service.IsTemporal("NotListed.fx"));
    }

    [Fact]
    public void Effects_beyond_the_per_tick_budget_read_as_size_locked_until_resolved()
    {
        var count = ReShadeService.DepthResolvesPerTick + 1;
        for (var i = 0; i < count; i++)
        {
            _fs.Add($"/pack/E{i}.fx", "float4 PS() { return 0; }");
            _native.Add($"T{i}", $"E{i}.fx");
        }
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        Tick();
        Assert.True(_service.Techniques[count - 1].SizeLocked);
        Assert.False(_service.Techniques[0].SizeLocked);
        Tick();
        Assert.All(_service.Techniques, t => Assert.False(t.SizeLocked));
    }

    [Fact]
    public void Effects_beyond_the_per_tick_budget_read_as_depth_until_resolved()
    {
        var count = ReShadeService.DepthResolvesPerTick * 2 + 1;
        for (var i = 0; i < count; i++)
        {
            _fs.Add($"/pack/E{i}.fx", "float4 PS() { return 0; }");
            _native.Add($"T{i}", $"E{i}.fx");
        }
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        Tick();
        Assert.True(_service.Techniques[count - 1].UsesDepth);
        Assert.False(_service.Techniques[0].UsesDepth);
        Tick();
        Tick();
        Assert.All(_service.Techniques, t => Assert.False(t.UsesDepth));
        Assert.Equal(3, _changed);
    }

    [Fact]
    public void A_reload_rechecks_the_effect_files()
    {
        _fs.Add("/pack/Clarity.fx", "float4 PS() { return 0; }");
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Assert.False(_service.Techniques[0].UsesDepth);
        _fs.Touch("/pack/Clarity.fx", "texture t : DEPTH;");
        _native.Loading = true;
        Tick();
        _native.Loading = false;
        Tick();
        Assert.True(_service.Techniques[0].UsesDepth);
    }

    [Fact]
    public void A_throwing_Changed_handler_is_logged_and_the_others_still_run()
    {
        var other = 0;
        _service.Changed += () => throw new InvalidOperationException("boom");
        _service.Changed += () => other++;
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Assert.Equal(1, _changed);
        Assert.Equal(1, other);
        Assert.Contains(_log.WarningLines, l => l.Contains("boom"));
    }

    // Final review: availability is a three-state lifecycle, not a bool.
    [Fact]
    public void State_is_NotInstalled_before_any_refresh_and_without_the_add_on()
    {
        Assert.Equal(ReShadeState.NotInstalled, _service.State);
        _native.IsLoaded = false;
        Tick();
        Assert.Equal(ReShadeState.NotInstalled, _service.State);
    }

    [Fact]
    public void State_is_Loading_while_bound_but_not_ready_or_reloading_and_Ready_after()
    {
        _native.Add("Clarity", "Clarity.fx");
        _native.Ready = false;
        Tick();
        Assert.Equal(ReShadeState.Loading, _service.State);
        _native.Ready = true;
        Tick();
        Assert.Equal(ReShadeState.Ready, _service.State);
        _native.Loading = true;
        Tick();
        Assert.Equal(ReShadeState.Loading, _service.State);
        _native.Loading = false;
        Tick();
        Assert.Equal(ReShadeState.Ready, _service.State);
        Assert.Equal(4, _changed);   // every state move raises Changed
    }

    [Fact]
    public void Losing_the_add_on_returns_to_NotInstalled()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        _native.IsLoaded = false;
        Tick();
        Assert.Equal(ReShadeState.NotInstalled, _service.State);
    }

    [Fact]
    public void Turning_effects_on_or_off_raises_Changed()
    {
        _native.Add("Clarity", "Clarity.fx");
        Tick();
        Assert.Equal(1, _changed);
        _native.EffectsEnabled = false;   // e.g. ReShade's own toggle key
        Tick();
        Assert.Equal(2, _changed);
        Assert.False(_service.Enabled);
        Tick();
        Assert.Equal(2, _changed);
        _native.EffectsEnabled = true;
        Tick();
        Assert.Equal(3, _changed);
    }

    [Fact]
    public void Identical_search_paths_are_sent_once_so_ReShade_does_not_reload_again()
    {
        for (var i = 0; i < 3; i++)
            _service.SetSearchPaths(new[] { "/pack" }, new[] { "/tex" });
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(new[] { $"paths effects=/pack{sep}** textures=/tex{sep}**" }, _native.Requests);

        _service.SetSearchPaths(new[] { "/other" }, new[] { "/tex" });   // a real change is still sent
        Assert.Equal(2, _native.Requests.Count);
    }

    [Fact]
    public void Identical_search_paths_held_before_bind_are_held_once()
    {
        _native.IsLoaded = false;
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        _native.IsLoaded = true;
        Tick();
        _service.SetSearchPaths(new[] { "/pack" }, Array.Empty<string>());
        Assert.Single(_native.Requests);
    }
}
