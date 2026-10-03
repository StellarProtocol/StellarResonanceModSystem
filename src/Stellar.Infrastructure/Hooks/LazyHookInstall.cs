using System;
namespace Stellar.Infrastructure.Hooks;

/// <summary>
/// Installs a set of game hooks once, and only when both armed (the hot-update assemblies are loaded, a hooker
/// exists) and requested (a plugin first asked for the feature). Until then the game runs unpatched; nothing is
/// held, so nothing needs re-asserting. Main thread. Pure, so it is unit-tested.
/// </summary>
internal sealed class LazyHookInstall
{
    private Action? _install;
    private bool _requested;

    public bool IsInstalled { get; private set; }

    /// <summary>Supplies the install action (call once hooks CAN be installed). Installs now if already requested.</summary>
    public void Arm(Action install)
    {
        _install ??= install;
        TryInstall();
    }

    /// <summary>The feature was first used. Installs now if armed.</summary>
    public void Request()
    {
        _requested = true;
        TryInstall();
    }

    private void TryInstall()
    {
        if (IsInstalled || !_requested || _install is null) return;
        IsInstalled = true;
        _install();
    }
}
