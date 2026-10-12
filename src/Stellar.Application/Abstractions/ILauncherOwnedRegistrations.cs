using System;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Owner-tagged launcher registration sink — the launcher counterpart of <see cref="IHotkeyOwnedDeclarations"/>.
/// <c>PerPluginLauncher</c> forwards each plugin's <see cref="ILauncher.Register"/> here tagged with the plugin's
/// guid, so the framework knows which plugin registered which <see cref="LauncherEntry"/> (used to show a plugin's
/// own localized tile title as its name in Settings — see <c>PluginDisplayNames</c>).
/// </summary>
internal interface ILauncherOwnedRegistrations
{
    /// <summary>Same as <see cref="ILauncher.Register"/>, recording <paramref name="ownerId"/> as the entry's owner.</summary>
    IDisposable Register(LauncherEntry entry, string ownerId);

    /// <summary>The first still-registered entry (registration order) owned by <paramref name="ownerId"/>, or null.</summary>
    LauncherEntry? FirstEntryOwnedBy(string ownerId);
}
