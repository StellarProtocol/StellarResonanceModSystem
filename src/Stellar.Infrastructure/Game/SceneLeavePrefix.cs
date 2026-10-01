using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>A PREFIX on <c>Panda.Core.Game.OnLeaveScene</c> — the same method the framework's lifecycle postfix
/// (<c>IClientState.SceneChanged(null)</c>) patches, installed through the same <see cref="HarmonyGameMethodHooker"/>.
/// It fires <see cref="Leaving"/> before the game's leave code runs, while every entity of the old scene is still
/// alive, so anything that writes other entities' models (the free camera's freeze hold and release snap) can hand
/// back first. Never throws into the game: a subscriber's exception is caught and warned once. Main thread.</summary>
internal sealed class SceneLeavePrefix
{
    private const string MethodName = "OnLeaveScene";
    private readonly IPluginLog _log;
    private bool _warned;

    public SceneLeavePrefix(IPluginLog log) => _log = log;

    /// <summary>Raised from the prefix, before the game leaves the scene.</summary>
    public event Action? Leaving;

    public void Install(HarmonyGameMethodHooker hooker, Type gameType) => hooker.PrefixAllOverloads(gameType, MethodName, OnPrefix);

    private void OnPrefix(object? instance, object?[] args)
    {
        var leaving = Leaving;
        if (leaving is null) return;
        try { leaving(); }
        catch (Exception ex)
        {
            if (_warned) return;
            _warned = true;
            _log.Warning("[FreeCam] scene-leave release threw (warned once): " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
