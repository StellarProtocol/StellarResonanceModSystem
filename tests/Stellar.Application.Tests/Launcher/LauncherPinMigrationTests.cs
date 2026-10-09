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

    [Fact]
    public void NoOp_WhenTitleAlreadyPinned()
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
