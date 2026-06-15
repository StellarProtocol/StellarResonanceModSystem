using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;

namespace Stellar.Application.Hosting;

/// <summary>
/// Discovers user plugins from a directory and hands each one to the
/// <see cref="PluginRegistry"/> as a factory delegate. Each plugin DLL is
/// expected to export a single non-abstract <see cref="IStellarPlugin"/>
/// with a constructor taking <see cref="IPluginServices"/>. Each loaded
/// plugin receives a per-plugin <see cref="IPluginConfig"/> minted by
/// <see cref="IPluginConfigFactory"/>, keyed by the plugin's assembly name
/// (lowercased) so config files don't collide.
/// </summary>
internal sealed class PluginHost : IDisposable
{
    private readonly IPluginServices _services;
    private readonly IPluginConfigFactory _configFactory;
    private readonly PluginRegistry _registry;
    private readonly IPluginLog _log;

    public PluginHost(IPluginServices services, IPluginConfigFactory configFactory, PluginRegistry registry)
    {
        _services = services;
        _configFactory = configFactory;
        _registry = registry;
        _log = services.Log;
    }

    public void LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            _log.Info($"[PluginHost] no plugin directory at {directory} — skipping");
            return;
        }

        var discovered = 0;
        foreach (var dll in Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                if (RegisterOne(dll)) discovered++;
            }
            catch (Exception ex)
            {
                _log.Error($"[PluginHost] failed to register {Path.GetFileName(dll)}: {ex.Message}");
            }
        }

        // Phase 8 wording kept for backwards-compat with the in-world scenario
        // regex; "discovered" matches the new soft-cycle semantics where the
        // registry decides whether each plugin is actually constructed.
        _log.Info($"[PluginHost] {discovered} plugin(s) discovered from {directory}");
        _log.Info($"[PluginHost] {discovered} plugin(s) loaded from {directory}");
    }

    private bool RegisterOne(string dllPath)
    {
        var asm = Assembly.LoadFile(dllPath);
        var pluginType = asm.GetTypes()
            .FirstOrDefault(t => !t.IsAbstract && typeof(IStellarPlugin).IsAssignableFrom(t));

        if (pluginType is null)
        {
            _log.Debug($"[PluginHost] {Path.GetFileName(dllPath)}: no IStellarPlugin implementation");
            return false;
        }

        var ctor = pluginType.GetConstructor(new[] { typeof(IPluginServices) });
        if (ctor is null)
        {
            _log.Warning($"[PluginHost] {pluginType.FullName}: missing (IPluginServices) constructor");
            return false;
        }

        // Per-plugin config: GUID derived from the plugin's assembly name,
        // lowercased. e.g. Stellar.StatInspector → stellar.statinspector.
        var pluginGuid = (asm.GetName().Name ?? pluginType.FullName ?? "unknown")
            .ToLowerInvariant();
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        // The display name lives on the plugin instance's Name property, which
        // requires construction. The PluginRegistry calls the factory on enable;
        // until the first successful enable, we fall back to the assembly's
        // short name for the Plugins panel listing.
        var displayName = asm.GetName().Name ?? pluginType.FullName ?? pluginGuid;

        // Capture ctor + per-plugin config in the factory delegate so the
        // registry can reconstruct the plugin on soft-cycle without re-running
        // the discovery walk.
        var perPluginConfig = _configFactory.Create(pluginGuid);
        Func<IPluginServices, object> factory = sharedServices =>
        {
            var perPluginServices = new PerPluginServices(sharedServices, perPluginConfig);
            var instance = (IStellarPlugin)ctor.Invoke(new object[] { perPluginServices });
            return instance;
        };

        _registry.Register(pluginGuid, displayName, version, factory);
        _log.Info($"[PluginHost] discovered: {pluginType.FullName} (config={pluginGuid})");
        return true;
    }

    public void Dispose()
    {
        // Delegated to PluginRegistry.DisposeAll — the registry owns plugin
        // instance lifetime now.
        _registry.DisposeAll();
    }
}
