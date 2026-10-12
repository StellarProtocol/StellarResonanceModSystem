// tests/Stellar.Application.Tests/Launcher/LauncherPinMigrationTests.cs
using System.Collections.Generic;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Launcher;

// Pins the pure decision behind the 8-plugin translated-Title -> fixed-Title+TitleProvider migration
// (owner-approved train, round-3 review follow-up): a user who pinned a plugin under its OLD translated
// LauncherEntry.Title must not lose that pin once the plugin starts shipping a fixed English Title with a
// TitleProvider for display.
public sealed class LauncherPinMigrationTests
{
    [Fact]
    public void MigratesTranslatedToTitle_KeepingOrder()
    {
        var pinned = new List<string> { "A", "컴뱃미터", "B" };

        var result = LauncherPinMigration.Migrate(pinned, title: "CombatMeter", displayTitle: "컴뱃미터");

        Assert.Equal(new[] { "A", "CombatMeter", "B" }, result);
    }

    // Renamed from the original "NoOp_WhenTitleAlreadyPinned" (owner review round 4, Minor #3): that name
    // suggested this exercises the "both already pinned" dedup branch, but Title being pinned here is
    // incidental — the function returns null on the FIRST check, "displayTitle isn't in the pinned set at
    // all", exactly like NoOp_WhenNeitherPresent below. This is still a genuine, distinct real-world
    // scenario (a user who already has the plugin pinned under its current Title, and never had the OLD
    // translated one pinned at all — e.g. a first-time pin after this fix shipped), so it stays as its own
    // test, just honestly named.
    [Fact]
    public void NoOp_WhenTitleAlreadyPinned_AndDisplayTitleWasNeverPinned()
    {
        var pinned = new List<string> { "A", "CombatMeter", "B" };

        // displayTitle ("컴뱃미터") isn't in the set at all — nothing stale to carry forward.
        var result = LauncherPinMigration.Migrate(pinned, title: "CombatMeter", displayTitle: "컴뱃미터");

        Assert.Null(result);
    }

    [Fact]
    public void NoOp_WhenNeitherPresent()
    {
        var pinned = new List<string> { "A", "B" };

        var result = LauncherPinMigration.Migrate(pinned, title: "CombatMeter", displayTitle: "컴뱃미터");

        Assert.Null(result);
    }

    [Fact]
    public void NoOp_WhenDisplayTitleEqualsTitle()
    {
        // An English-only plugin (or a TitleProvider that happens to resolve to the same text) — never
        // diverged, so even though "CombatMeter" IS in the set, there's nothing to migrate.
        var pinned = new List<string> { "A", "CombatMeter" };

        var result = LauncherPinMigration.Migrate(pinned, title: "CombatMeter", displayTitle: "CombatMeter");

        Assert.Null(result);
    }

    [Fact]
    public void DoesNotCreateDuplicates_WhenBothPresent_DropsTheStaleTranslatedOne()
    {
        // The user pinned under the old translated title, THEN pinned again under the new title before this
        // migration ever ran — both keys are in the set. The fix must not leave two entries for one plugin.
        var pinned = new List<string> { "A", "컴뱃미터", "CombatMeter", "B" };

        var result = LauncherPinMigration.Migrate(pinned, title: "CombatMeter", displayTitle: "컴뱃미터");

        Assert.Equal(new[] { "A", "CombatMeter", "B" }, result);
    }
}
