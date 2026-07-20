using Stellar.Infrastructure.Configuration;
using Xunit;

namespace Stellar.Application.Tests;

// Pins the shadow-load-hazard fix (Phase-A whole-branch review finding, 2026-07-21): the
// per-plugin data root must never be nested inside the directory PluginHost.LoadFrom recursively
// scans for *.dll and Assembly.LoadFile's (SearchOption.AllDirectories). A data file that happened
// to be named *.dll under the scan subtree would get shadow-loaded as a plugin — see CLAUDE.md's
// "NEVER put a copy inside a scan path" section. Do not weaken or delete this test.
public class FrameworkPathsTests
{
    [Fact]
    public void PluginData_dir_is_never_inside_the_plugin_scan_path()
    {
        var scan = FrameworkPaths.PluginScanSubdir.Replace('\\', '/');
        var data = FrameworkPaths.PluginDataSubdir.Replace('\\', '/');

        Assert.NotEqual(scan, data);
        Assert.False(data.StartsWith(scan.TrimEnd('/') + "/"), $"'{data}' must not be nested under the plugin-scan dir '{scan}' — it would be shadow-loaded by PluginHost.LoadFrom's recursive DLL scan.");
    }
}
