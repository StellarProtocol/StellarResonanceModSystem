using BepInEx.Logging;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.BepInExAdapters;

/// <summary>Adapts BepInEx's <see cref="ManualLogSource"/> to <see cref="IPluginLog"/>.</summary>
public sealed class BepInExPluginLog : IPluginLog
{
    private readonly ManualLogSource _source;

    public BepInExPluginLog(ManualLogSource source) => _source = source;

    public void Info(string message) => _source.LogInfo(message);
    public void Warning(string message) => _source.LogWarning(message);
    public void Error(string message) => _source.LogError(message);
    public void Debug(string message) => _source.LogDebug(message);
}
