using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Stellar.Abstractions.Domain.GameData;
using Xunit;

namespace Stellar.Application.Tests.GameData;

/// <summary>
/// Pins <see cref="ProfessionSpecs.TalentSchool"/> against the game's own
/// <c>TalentSchoolTable.json</c> (release_3.7).
///
/// Caught bug (2026-10): Twin Striker's two specs (30001 Formless, 30002 Crimson) mapped to talent-school
/// ids 124/125 — the table has NO row 124, and 125 is "Hand Cannon" (an unrelated school, not a Twin
/// Striker spec at all). The real ids are 128 ("Formless Spec") and 129 ("Crimson Spec").
///
/// CI constraint: this repo's own <c>ci.yml</c> checks out only this repo (no submodules), and
/// <c>data/StarResonanceData</c> lives in the sibling stellar-devkit superproject — it is never present
/// on CI regardless of checkout options. So the pinned regression (<see cref="TalentSchool_matches_the_games_own_table"/>)
/// uses an embedded, hand-transcribed copy of the table rows every <c>ProfessionSpecs</c> sub-profession id
/// can resolve to, rather than reading the JSON. <see cref="Fixture_matches_the_live_game_table_when_present"/>
/// is a local-only cross-check: when the data submodule IS checked out (devkit, not CI), it reads the real
/// JSON and verifies the embedded fixture hasn't drifted from it; it no-ops when the file is absent.
/// </summary>
public sealed class ProfessionSpecsTalentSchoolTests
{
    // (subProfessionId, talentSchoolId, schoolName) for every sub-profession id ProfessionSpecs knows
    // (mirrors SubProfessionNames 1:1 — 18 entries), verified by hand against
    // data/StarResonanceData/tables/TalentSchoolTable.json (release_3.7).
    public static IEnumerable<object[]> Expected() => new[]
    {
        new object[] { 10001,  101, "Iaido Slash Spec" },   // Stormblade: Iaido
        new object[] { 10002,  102, "Moonstrike Spec" },    // Stormblade: Moonstrike
        new object[] { 20001,  104, "Icicle Spec" },        // Frost Mage: Icicle
        new object[] { 20002,  105, "Frostbeam Spec" },     // Frost Mage: Frostbeam
        new object[] { 30001,  128, "Formless Spec" },      // Twin Striker: Formless
        new object[] { 30002,  129, "Crimson Spec" },       // Twin Striker: Crimson
        new object[] { 40001,  107, "Vanguard Spec" },      // Wind Knight: Vanguard
        new object[] { 40002,  108, "Skyward Spec" },       // Wind Knight: Skyward
        new object[] { 50001,  110, "Smite Spec" },         // Verdant Oracle: Smite
        new object[] { 50002,  111, "Lifebind Spec" },      // Verdant Oracle: Lifebind
        new object[] { 90001,  113, "Earthfort Spec" },     // Heavy Guardian: Earthfort
        new object[] { 90002,  114, "Block Spec" },         // Heavy Guardian: Block
        new object[] { 110001, 116, "Wildpack Spec" },      // Marksman: Wildpack
        new object[] { 110002, 117, "Falconry Spec" },      // Marksman: Falconry
        new object[] { 120001, 122, "Recovery Spec" },      // Shield Knight: Recovery
        new object[] { 120002, 123, "Shield Spec" },        // Shield Knight: Shield
        new object[] { 130001, 119, "Dissonance Spec" },    // Beat Performer: Dissonance
        new object[] { 130002, 120, "Concerto Spec" },      // Beat Performer: Concerto
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public void TalentSchool_matches_the_games_own_table(int subProfessionId, int expectedSchoolId, string expectedSchoolName)
    {
        var schoolId = ProfessionSpecs.TalentSchool(subProfessionId);
        Assert.Equal(expectedSchoolId, schoolId);

        // The embedded table's own row for that school id must carry the expected name — this is what
        // would have caught the 124/125 bug: 124 doesn't exist in the table (Hand Cannon's stage range)
        // and 125 IS a real row, just the wrong school ("Hand Cannon", not a Twin Striker spec).
        Assert.True(
            EmbeddedTalentSchoolRows.TryGetValue(schoolId, out var name),
            $"school id {schoolId} has no row in the embedded release_3.7 TalentSchoolTable snapshot");
        Assert.Equal(expectedSchoolName, name);
    }

    [Fact]
    public void Expected_fixture_covers_all_18_known_subprofessions()
        => Assert.Equal(18, Expected().Count());

    [Fact]
    public void Fixture_matches_the_live_game_table_when_present()
    {
        var path = FindDataFile();
        if (path is null) return; // data/StarResonanceData submodule not checked out (e.g. CI) — no-op.

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var live = new Dictionary<int, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            live[int.Parse(prop.Name)] = prop.Value.GetProperty("SchoolName").GetString()!;

        foreach (var row in Expected())
        {
            var schoolId = (int)row[1];
            var expectedName = (string)row[2];
            Assert.True(live.TryGetValue(schoolId, out var liveName), $"school id {schoolId} missing from live table");
            Assert.Equal(expectedName, liveName);
        }

        // The two historically-wrong ids: 124 must still not exist, 125 must still be a DIFFERENT school
        // (otherwise this cross-check would stop meaning anything if the game ever adds/renames them).
        Assert.False(live.ContainsKey(124));
        Assert.Equal("Hand Cannon", live[125]);
    }

    // A faithful subset of data/StarResonanceData/tables/TalentSchoolTable.json — every row
    // ProfessionSpecs.TalentSchool can return, plus the two "trap" rows (124 absent, 125 = Hand Cannon)
    // that make a wrong id fail loudly instead of silently resolving to *some* plausible-looking name.
    private static readonly Dictionary<int, string> EmbeddedTalentSchoolRows = new()
    {
        { 101, "Iaido Slash Spec" },
        { 102, "Moonstrike Spec" },
        { 104, "Icicle Spec" },
        { 105, "Frostbeam Spec" },
        { 107, "Vanguard Spec" },
        { 108, "Skyward Spec" },
        { 110, "Smite Spec" },
        { 111, "Lifebind Spec" },
        { 113, "Earthfort Spec" },
        { 114, "Block Spec" },
        { 116, "Wildpack Spec" },
        { 117, "Falconry Spec" },
        { 119, "Dissonance Spec" },
        { 120, "Concerto Spec" },
        { 122, "Recovery Spec" },
        { 123, "Shield Spec" },
        { 125, "Hand Cannon" },  // trap: Twin Striker Crimson's old (wrong) id — a real row, wrong school.
        { 126, "Hand Cannon" },
        { 128, "Formless Spec" },
        { 129, "Crimson Spec" },
        // 124 is deliberately absent — Twin Striker Formless's old (wrong) id; the table has no such row.
    };

    private static string? FindDataFile()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null)
        {
            var candidate = Path.Combine(d.FullName, "data", "StarResonanceData", "tables", "TalentSchoolTable.json");
            if (File.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        return null;
    }
}
