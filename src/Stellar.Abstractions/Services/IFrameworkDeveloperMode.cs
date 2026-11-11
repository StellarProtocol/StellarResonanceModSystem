using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// The user's framework developer mode, read-only for plugins (2.30.0). Split out of <see cref="IFramework"/>, which is
/// at the framework's interface-size budget; consumers reach it through <c>services.Framework</c> exactly as if it were
/// declared there.
/// <para>Developer mode is the switch a player turns on from Settings (7 clicks on the version on the About tab, or
/// <c>"developer": { "developerMode": true }</c> in <c>stellar.framework.config.json</c>). Use it to show your plugin's
/// debug tools, extra diagnostics and verbose logs only while the user has it on — normal players never see them.
/// Plugins can only read it; turning it on or off is the user's choice.</para>
/// </summary>
public interface IFrameworkDeveloperMode
{
    /// <summary>Whether developer mode is on right now. Read live: a cached field read, cheap enough to check every
    /// tick or from an element <c>Func</c>. False until the framework has loaded its settings.</summary>
    bool DeveloperMode { get; }

    /// <summary>Raised on the Unity main thread with the new value whenever developer mode is switched on or off — from
    /// Settings or by a hand edit of the framework config file. Not raised for your plugin's current value at subscribe
    /// time: read <see cref="DeveloperMode"/> once, then follow this. Your subscription is dropped automatically when
    /// your plugin is disabled or unloaded. A throwing handler is logged and never blocks other plugins' handlers.</summary>
    event Action<bool> DeveloperModeChanged;
}
