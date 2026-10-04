using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// Uniform overrides (bridge 1.1.0): hold a uniform at a value in every ReShade runtime — needed because AcerolaFX's
// _MaskUI (default true) discards its output in this game and ReShade resets it on every effect reload.
public sealed class ReShadeUniformOverrideTests
{
    private readonly FakeReShadeNative _native = new();
    private readonly ReShadeService _service;

    public ReShadeUniformOverrideTests()
    {
        var fs = new InMemoryEffectFiles();
        _service = new ReShadeService(_native, new EffectDepthIndex(fs), new EffectSizeLockIndex(fs), new EffectTemporalIndex(fs), new NullLog());
    }

    private void Tick()
    {
        _native.NextFrame();
        _service.Refresh();
    }

    private IEnumerable<string> UniformRequests => _native.Requests.Where(r => r.StartsWith("uniform ", StringComparison.Ordinal));

    [Theory]
    [InlineData("0")]
    [InlineData("1,0,0")]
    [InlineData(" 0.5 , -2e3 ")]
    [InlineData("true")]
    [InlineData("FALSE")]
    [InlineData("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16")]
    public void Valid_values(string value) => Assert.True(UniformOverrideValue.IsValid(value));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,,2")]
    [InlineData("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17")]
    [InlineData("NaN")]
    [InlineData("1;2")]
    public void Invalid_values(string value) => Assert.False(UniformOverrideValue.IsValid(value));

    [Fact]
    public void A_bound_1_1_bridge_gets_the_override_at_once()
    {
        Tick();
        Assert.True(_service.SetUniformOverride("AcerolaFX_End.fx", "_MaskUI", "false"));
        Assert.Equal(new[] { "uniform AcerolaFX_End.fx/_MaskUI=false" }, UniformRequests);
    }

    [Fact]
    public void Null_or_empty_effect_means_every_effect()
    {
        Tick();
        Assert.True(_service.SetUniformOverride(null, "_MaskUI", "0"));
        Assert.True(_service.SetUniformOverride("", "Other", "1"));
        Assert.Equal(new[] { "uniform <all>/_MaskUI=0", "uniform <all>/Other=1" }, UniformRequests);
    }

    [Fact]
    public void A_null_value_removes_the_override()
    {
        Tick();
        _service.SetUniformOverride("A.fx", "_MaskUI", "0");
        Assert.True(_service.SetUniformOverride("A.fx", "_MaskUI", null));
        Assert.Equal("uniform A.fx/_MaskUI=<remove>", UniformRequests.Last());
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("  ", "0")]
    [InlineData("_MaskUI", "maybe")]
    public void Bad_input_is_refused_and_never_sent(string variable, string value)
    {
        Tick();
        Assert.False(_service.SetUniformOverride("A.fx", variable, value));
        Assert.Empty(UniformRequests);
    }

    [Fact]
    public void A_value_the_add_on_rejects_returns_false_and_is_not_held()
    {
        Tick();
        _native.RejectUniformValues = true;
        Assert.False(_service.SetUniformOverride("A.fx", "_MaskUI", "1,2"));
        _native.RejectUniformValues = false;
        _native.IsLoaded = false;
        Tick();
        _native.IsLoaded = true;
        Tick();   // re-bind: nothing held to re-send
        Assert.Single(UniformRequests);
    }

    [Fact]
    public void A_1_0_bridge_returns_false_and_sends_nothing()
    {
        _native.UniformOverridesSupported = false;
        Tick();
        Assert.False(_service.SetUniformOverride("A.fx", "_MaskUI", "0"));
        _service.ClearUniformOverrides();
        Assert.DoesNotContain(_native.Requests, r => r.StartsWith("uniform", StringComparison.Ordinal));
    }

    [Fact]
    public void Overrides_set_before_the_add_on_binds_are_held_then_sent_once_it_does()
    {
        _native.IsLoaded = false;
        Tick();
        Assert.True(_service.SetUniformOverride("A.fx", "_MaskUI", "0"));
        Assert.True(_service.SetUniformOverride("B.fx", "X", "1"));
        Assert.True(_service.SetUniformOverride("B.fx", "X", null));   // removed before it was ever sent
        Assert.Empty(UniformRequests);

        _native.IsLoaded = true;
        Tick();
        Assert.Equal(new[] { "uniform A.fx/_MaskUI=0" }, UniformRequests);
        Tick();
        Assert.Single(UniformRequests);   // sent once, not every tick
    }

    [Fact]
    public void Clear_sends_the_clear_and_forgets_the_held_ones()
    {
        _native.IsLoaded = false;
        Tick();
        _service.SetUniformOverride("A.fx", "_MaskUI", "0");
        _native.IsLoaded = true;
        Tick();
        _service.ClearUniformOverrides();
        Assert.Contains("uniforms clear", _native.Requests);
        _native.IsLoaded = false;
        Tick();
        _native.IsLoaded = true;
        Tick();   // a re-bind re-sends what is held: nothing
        Assert.Single(UniformRequests);
    }

    // ---- per plugin: a plugin's overrides are its own and are removed when it unloads.

    private sealed class RecordingUniforms : IReShadeUniforms
    {
        public readonly List<string> Calls = new();
        public bool Result = true;
        public bool SetUniformOverride(string? effectFile, string variable, string? value)
        {
            Calls.Add($"{effectFile ?? ""}/{variable}={value ?? "<remove>"}");
            return Result;
        }
        public void ClearUniformOverrides() => Calls.Add("clear-all");
    }

    [Fact]
    public void A_plugins_overrides_are_removed_on_unload()
    {
        var shared = new RecordingUniforms();
        var plugin = new PluginReShadeUniforms(shared);
        plugin.SetUniformOverride("A.fx", "_MaskUI", "0");
        plugin.SetUniformOverride(null, "X", "1");
        plugin.ReleaseAll();
        Assert.Equal(new[] { "A.fx/_MaskUI=0", "/X=1", "A.fx/_MaskUI=<remove>", "/X=<remove>" }, shared.Calls);
    }

    [Fact]
    public void A_plugins_clear_removes_only_its_own_overrides()
    {
        var shared = new RecordingUniforms();
        var plugin = new PluginReShadeUniforms(shared);
        plugin.SetUniformOverride("A.fx", "_MaskUI", "0");
        plugin.ClearUniformOverrides();
        plugin.ReleaseAll();   // nothing left to remove
        Assert.Equal(new[] { "A.fx/_MaskUI=0", "A.fx/_MaskUI=<remove>" }, shared.Calls);
        Assert.DoesNotContain("clear-all", shared.Calls);
    }

    [Fact]
    public void A_refused_or_removed_override_is_not_tracked()
    {
        var shared = new RecordingUniforms { Result = false };
        var plugin = new PluginReShadeUniforms(shared);
        Assert.False(plugin.SetUniformOverride("A.fx", "_MaskUI", "bad"));
        shared.Result = true;
        plugin.SetUniformOverride("B.fx", "Y", "1");
        plugin.SetUniformOverride("B.fx", "Y", null);
        shared.Calls.Clear();
        plugin.ReleaseAll();
        Assert.Empty(shared.Calls);
    }

    [Fact]
    public void Without_a_uniform_capable_service_the_null_object_refuses()
    {
        Assert.False(UnavailableReShadeUniforms.Instance.SetUniformOverride("A.fx", "_MaskUI", "0"));
        UnavailableReShadeUniforms.Instance.ClearUniformOverrides();
    }

    private sealed class NullLog : IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
