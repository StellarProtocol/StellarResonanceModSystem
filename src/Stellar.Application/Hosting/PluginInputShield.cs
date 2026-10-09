using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>Per-plugin view of <see cref="InputShieldService"/>; unload drops the plugin's handles.</summary>
internal sealed class PluginInputShield : IInputShield
{
    private readonly InputShieldService _inner;
    private readonly object _owner;

    public PluginInputShield(InputShieldService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public IInputShieldHandle Shield() => _inner.Shield(_owner);
    public bool IsShielded => _inner.IsShielded;
    public bool IsPointerOverGameUi => _inner.IsPointerOverGameUi;
    public void ReleaseAll() => _inner.ReleaseOwner(_owner);
}
