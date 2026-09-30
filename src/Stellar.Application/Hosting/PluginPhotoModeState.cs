using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin view of the shared <see cref="IPhotoModeState"/>. On plugin unload <see cref="ReleaseAll"/>
/// unsubscribes every <see cref="Entered"/> / <see cref="Exited"/> / <see cref="CutsceneChanged"/> handler the plugin
/// added through this façade, so an unloaded plugin is never called back (mirrors <see cref="PluginSceneVisibility"/>).
/// </summary>
internal sealed class PluginPhotoModeState : IPhotoModeState
{
    private readonly IPhotoModeState _inner;
    private readonly List<Action<PhotoModeKind>> _entered = new();
    private readonly List<Action> _exited = new();
    private readonly List<Action<bool>> _cutscene = new();

    public PluginPhotoModeState(IPhotoModeState inner) => _inner = inner;

    public bool IsActive => _inner.IsActive;
    public PhotoModeKind Kind => _inner.Kind;
    public bool InCutscene => _inner.InCutscene;

    public event Action<PhotoModeKind>? Entered
    {
        add { if (value is null) return; _entered.Add(value); _inner.Entered += value; }
        remove { if (value is not null && _entered.Remove(value)) _inner.Entered -= value; }
    }

    public event Action? Exited
    {
        add { if (value is null) return; _exited.Add(value); _inner.Exited += value; }
        remove { if (value is not null && _exited.Remove(value)) _inner.Exited -= value; }
    }

    public event Action<bool>? CutsceneChanged
    {
        add { if (value is null) return; _cutscene.Add(value); _inner.CutsceneChanged += value; }
        remove { if (value is not null && _cutscene.Remove(value)) _inner.CutsceneChanged -= value; }
    }

    public void ReleaseAll()
    {
        foreach (var h in _entered) _inner.Entered -= h;
        foreach (var h in _exited) _inner.Exited -= h;
        foreach (var h in _cutscene) _inner.CutsceneChanged -= h;
        _entered.Clear();
        _exited.Clear();
        _cutscene.Clear();
    }
}
