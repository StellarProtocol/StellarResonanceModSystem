// src/Stellar.Abstractions/Services/IPluginConfig.cs
using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-plugin persistent configuration. Each plugin gets one JSON file on disk
/// (<c>&lt;plugin-dir&gt;/&lt;pluginGuid&gt;.config.json</c>) organized into
/// named sections. Plugins read settings on construct, write whenever the user
/// changes them, and subscribe to <see cref="SectionChanged"/> to react to
/// external edits or settings-window writes from sibling code.
/// </summary>
public interface IPluginConfig
{
    /// <summary>
    /// Returns the named section. Sections are created lazily on first access;
    /// the returned <see cref="IConfigSection"/> is stable across calls for
    /// the same name.
    /// </summary>
    IConfigSection GetSection(string name);

    /// <summary>
    /// Fired when any section's underlying file changes — either via
    /// <see cref="IConfigSection.Save"/> from this process or an external edit
    /// detected by the file watcher. The string argument is the section name.
    /// Subscribers are called on the game thread.
    /// </summary>
    event Action<string>? SectionChanged;
}
