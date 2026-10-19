using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Owner-tagged window registration sink — the window counterpart of <see cref="IHotkeyOwnedDeclarations"/> and
/// <see cref="ILauncherOwnedRegistrations"/>. <c>PerPluginWindowHost</c> forwards each plugin's
/// <see cref="IWindowHost.Register(WindowRegistration)"/> here tagged with the plugin's guid, so the framework
/// knows which plugin owns which window (used by the layout editor to filter edit-mode to one plugin's windows,
/// and to name that plugin via <c>PluginDisplayNames</c>). The framework's own windows register untagged (null
/// owner) through the plain public <see cref="IWindowHost"/> methods.
/// </summary>
internal interface IWindowOwnedRegistrations
{
    /// <summary>Same as <see cref="IWindowHost.Register(WindowRegistration)"/>, recording <paramref name="ownerId"/>
    /// as the window's owner.</summary>
    IWindowControl Register(WindowRegistration registration, string ownerId);

    /// <summary>Same as <see cref="IWindowHost.Register(WindowRegistration, HotkeyAction, IHotkeys)"/>, recording
    /// <paramref name="ownerId"/> as the window's owner.</summary>
    IWindowControl Register(WindowRegistration registration, HotkeyAction toggleAction, IHotkeys hotkeys, string ownerId);
}
