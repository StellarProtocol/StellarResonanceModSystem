using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin view of the shared <see cref="IRenderLook"/> (spec § 6 "framework backstop"). Tracks every
/// <see cref="ILookHandle"/> it hands out; on plugin unload <see cref="ReleaseAll"/> disposes them, so a plugin
/// unloaded while its look is live never leaves the game re-graded. Disposing a handle another plugin's Apply
/// already replaced is a no-op (the service ignores stale handles), so a release never turns off someone else's look.
/// </summary>
internal sealed class PluginRenderLook : IRenderLook
{
    private readonly IRenderLook _inner;
    private readonly List<ILookHandle> _handles = new();

    public PluginRenderLook(IRenderLook inner) => _inner = inner;

    public LookCapabilities Capabilities => _inner.Capabilities;

    /// <summary>Handles still held (diagnostics / tests).</summary>
    internal int TrackedCount => _handles.Count;

    public ILookHandle Apply(LookSettings settings)
    {
        _handles.RemoveAll(h => !h.IsActive);   // disposed / replaced handles need no backstop
        var handle = _inner.Apply(settings);
        _handles.Add(handle);
        return handle;
    }

    public void ReleaseAll()
    {
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
    }
}
