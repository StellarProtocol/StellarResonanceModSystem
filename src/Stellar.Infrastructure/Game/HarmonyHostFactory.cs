using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IHarmonyHostFactory"/> implementation. Lives in Infrastructure because the hosts it mints own
/// <see cref="HarmonyLib.Harmony"/> instances (the game-interop layer). Stateless apart from the shared log sink.
/// </summary>
internal sealed class HarmonyHostFactory : IHarmonyHostFactory
{
    private readonly Action<string>? _log;

    public HarmonyHostFactory(Action<string>? log = null) => _log = log;

    public IHarmonyHost Create(string pluginId) => new PerPluginHarmonyHost(pluginId, _log);
}
