using System;
using System.Diagnostics.CodeAnalysis;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>Per-plugin view of <see cref="CameraOverrideService"/>. <see cref="ReleaseAll"/> (unload) first detaches every
/// handler the plugin added (an unloaded plugin is never called back), then ends its override and look-at.</summary>
internal sealed class PluginCameraOverride : ICameraOverride
{
    private readonly CameraOverrideService _inner;
    private readonly object _owner;
    private readonly HandlerList<Action<CameraReleaseReason>> _released = new();

    public PluginCameraOverride(CameraOverrideService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public bool TryAcquire([NotNullWhen(true)] out ICameraControl? control) => _inner.TryAcquire(_owner, out control);
    public bool IsOverridden => _inner.IsOverridden;
    public IDisposable LookAtCamera() => _inner.LookAtCamera(_owner);

    public event Action<CameraReleaseReason>? Released
    {
        add => _released.Add(value, h => _inner.Released += h);
        remove => _released.Remove(value, h => _inner.Released -= h);
    }

    public void ReleaseAll()
    {
        _released.Clear(h => _inner.Released -= h);
        _inner.ReleaseOwner(_owner);
    }
}
