// src/Stellar.Infrastructure/Configuration/FileConfigStore.Diagnostics.cs
using System.Text.Json.Nodes;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Configuration;

/// <summary>
/// Diagnostic-mode logging for <see cref="FileConfigStore"/>. All entry points
/// short-circuit on <see cref="StellarDiagnostics.IsEnabled"/> so the
/// production partial can call them unconditionally — keeps the hot path clean
/// of inline gates (per coding-standards § Diagnostics).
/// </summary>
internal sealed partial class FileConfigStore
{
    private void LogLoaded(string pluginGuid, int byteCount, JsonNode? root)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var sectionCount = root is JsonObject obj ? obj.Count : 0;
        _log.Info($"[Stellar][PluginConfig] loaded plugin config: {pluginGuid} ({byteCount}bytes, {sectionCount}sections)");
    }

    private void LogSaved(string pluginGuid, int byteCount)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][PluginConfig] saved: {pluginGuid} ({byteCount}bytes)");
    }

    private void LogEchoSuppressed(string pluginGuid)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][PluginConfig] echo suppressed: {pluginGuid}");
    }

    private void LogExternalEditQueued(string pluginGuid)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][PluginConfig] external edit detected: {pluginGuid} (queued)");
    }

    private void LogDeleted(string? fileName)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][PluginConfig] config file deleted: {fileName}");
    }
}
