using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin view of <see cref="RenderQualityService"/>. Every token is tagged with this façade's owner key; on
/// plugin unload <see cref="ReleaseAll"/> releases them, so an unloaded plugin never leaves the game supersampled.
/// </summary>
internal sealed class PluginRenderQuality : IRenderQuality
{
    private readonly RenderQualityService _inner;
    private readonly object _owner;

    public PluginRenderQuality(RenderQualityService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public IDisposable Request(RenderQualityRequest request) => _inner.Request(request, _owner);
    public RenderQualityState Live => _inner.Live;
    public RenderQualityCapabilities Capabilities => _inner.Capabilities;

    public void ReleaseAll() => _inner.ReleaseOwner(_owner);
}
