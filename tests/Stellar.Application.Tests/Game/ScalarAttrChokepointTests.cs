using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>Structural pin (fix round 1, hardened round 2): the probe stores a scalar attr in exactly ONE
/// place — PandaCombatStubProbe.AttrEvents.cs's StoreScalarAttr, which writes the sink and records the pair
/// together. A bare `_sink.SetEntityAttribute(` anywhere else under src/Stellar.Infrastructure is an unpaired
/// write: the attr reaches GetAttributes but is silently missing from that packet's EntityAttributesChanged,
/// so the rDPS sheet drifts from what the game actually sent. That is exactly the defect this pins —
/// Vitals.cs's ApplyParsedDelta wrote FightPoint straight to the sink. The scan covers every *.cs file under
/// Infrastructure (not just the PandaCombatStubProbe*.cs family) so a chokepoint bypass anywhere in the
/// layer is caught, and skips comment-only lines (`//`/`///`) so a documentation mention of the literal
/// inside Infrastructure source (a code comment explaining the chokepoint, say) never trips it.
/// Route new writes through the chokepoint; do NOT relax this test to a count.</summary>
public sealed class ScalarAttrChokepointTests
{
    const string SinkWrite = "_sink.SetEntityAttribute(";

    [Fact]
    public void Every_scalar_attr_write_goes_through_the_StoreScalarAttr_chokepoint()
    {
        var infraDir = Path.Combine(RepoRoot(), "src", "Stellar.Infrastructure");
        var files = Directory.GetFiles(infraDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var hits = new List<string>();
        foreach (var file in files)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
             || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // covers "///" too
                if (line.Contains(SinkWrite, StringComparison.Ordinal)) hits.Add(Path.GetFileName(file));
            }
        }

        Assert.Equal(new[] { "PandaCombatStubProbe.AttrEvents.cs" }, hits.ToArray());
    }

    /// <summary>Walks up from the test binary to the framework repo root (the dir holding src/Stellar.sln).</summary>
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Stellar.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException($"No ancestor of '{AppContext.BaseDirectory}' contains src/Stellar.sln.");
        return dir.FullName;
    }
}
