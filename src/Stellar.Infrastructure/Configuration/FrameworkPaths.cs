namespace Stellar.Infrastructure.Configuration;

/// <summary>
/// Single source of truth for the framework's two game-root-relative directories. The data subdir
/// MUST stay OUTSIDE the plugin-scan subtree: <see cref="Stellar.Application.Hosting.PluginHost.LoadFrom"/>
/// recursively <c>Assembly.LoadFile</c>s every <c>*.dll</c> found under the scan dir
/// (<c>SearchOption.AllDirectories</c>). A per-plugin data file that happened to be a DLL sitting
/// inside the scan subtree would get shadow-loaded as if it were a plugin — the exact hazard
/// CLAUDE.md's "NEVER put a copy inside a scan path" section forbids. Keeping the data root as a
/// sibling, not a descendant, of the scan dir makes that hazard structurally impossible.
/// </summary>
internal static class FrameworkPaths
{
    /// <summary>Game-root-relative directory <see cref="Stellar.Application.Hosting.PluginHost"/> recursively scans for plugin DLLs.</summary>
    internal const string PluginScanSubdir = "stellar/plugins";

    /// <summary>Game-root-relative directory under which each plugin's <c>&lt;guid&gt;.data/</c> store is rooted. A sibling of <see cref="PluginScanSubdir"/>, never a descendant.</summary>
    internal const string PluginDataSubdir = "stellar/plugindata";
}
