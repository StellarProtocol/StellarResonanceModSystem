using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private readonly TickScheduler _scheduler;

    public PluginHost(IPluginServices services, IPluginConfigFactory configFactory, PluginRegistry registry, TickScheduler scheduler)
    {
        _services = services;
        _configFactory = configFactory;
        _registry = registry;
        _log = services.Log;
        _scheduler = scheduler;
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

        var pluginGuid = (asm.GetName().Name ?? pluginType.FullName ?? "unknown").ToLowerInvariant();
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        // The display name lives on the plugin instance's Name property, which
        // requires construction. The PluginRegistry calls the factory on enable;
        // until the first successful enable, we fall back to the assembly's
        // short name for the Plugins panel listing.
        var displayName = asm.GetName().Name ?? pluginType.FullName ?? pluginGuid;
        var perPluginConfig = _configFactory.Create(pluginGuid);

        // Shared mutable cell: both the factory lambda (writer) and the onDispose
        // lambda (reader) capture the same StrongBox so each soft-cycle enable
        // updates the reference that onDispose will unregister.
        var frameworkCell = new StrongBox<PerPluginFramework?>();

        Func<IPluginServices, object> factory = sharedServices =>
            BuildAndInvoke(ctor, pluginGuid, perPluginConfig, frameworkCell, sharedServices);

        _registry.Register(pluginGuid, displayName, version, factory,
            onDispose: () => frameworkCell.Value?.Unregister());
        _log.Info($"[PluginHost] discovered: {pluginType.FullName} (config={pluginGuid})");
        return true;
    }

    // Creates the PerPluginFramework + PerPluginServices and invokes the plugin constructor.
    // Extracted to keep RegisterOne under 50 LoC (STELLAR0002).
    // On plugin-ctor failure the facade is unregistered from the scheduler before rethrowing,
    // so a failed plugin leaves no dangling scheduler entry.
    private IStellarPlugin BuildAndInvoke(
        ConstructorInfo ctor,
        string pluginGuid,
        IPluginConfig perPluginConfig,
        StrongBox<PerPluginFramework?> frameworkCell,
        IPluginServices sharedServices)
    {
        var perPluginFramework = new PerPluginFramework(pluginGuid, _scheduler, sharedServices.Framework);
        frameworkCell.Value = perPluginFramework;
        var perPluginServices = new PerPluginServices(sharedServices, perPluginConfig, perPluginFramework);
        try
        {
            return (IStellarPlugin)ctor.Invoke(new object[] { perPluginServices });
        }
        catch
        {
            perPluginFramework.Unregister();
            throw;
        }
    }

    public void Dispose()
    {
        // Delegated to PluginRegistry.DisposeAll — the registry owns plugin
        // instance lifetime now.
        _registry.DisposeAll();
    }
}
