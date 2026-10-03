using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Lights;

// Lights review 2026-10-03 (qa of fw 19e9d2a), the framework findings: I-1 read before EVERY write (a material gained between
// two applies is restored), I-2 a gate write that throws never strands the game's volume, I-4 the gate is restored only while
// the volume still holds exactly what we wrote (the game owns it otherwise) and an already-active volume keeps the game's own
// overrides, concern 1 a posed copy born lit is restored to the lit model's originals, and the model-level liveness check is
// gone from the write-back (each material is checked).
public sealed class LightsReviewFixTests
{
    private static readonly PersonLight KeyAndRim =
        new(new KeyLight(-60f, 25f), new RimLight(new RgbColor(1f, 0.45f, 0.15f), 0.45f));

    // ── I-1 ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void I1_a_material_added_between_two_applies_is_restored()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        Assert.True(r.Svc.SetPersonLight(new EntityId(1), KeyAndRim));
        var cape = new FakeMaterial(r.Backend.Log, "you.cape");             // equipment change while lit
        var capeOriginal = cape.Copy();
        you.Mats = you.Mats.Append(cape).ToArray();
        Assert.True(r.Svc.SetPersonLight(new EntityId(1), KeyAndRim with { Key = new KeyLight(30f, 10f) }));
        Assert.NotEqual(capeOriginal[LightProperty.CameraLightParm], cape.Values[LightProperty.CameraLightParm]);   // lit now
        r.Svc.SetPersonLight(new EntityId(1), PersonLight.None);
        foreach (var (k, v) in capeOriginal) Assert.Equal(v, cape.Values[k]);
    }

    [Fact]
    public void I1_the_new_material_is_read_before_the_write_that_lights_it()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        r.Svc.SetPersonLight(new EntityId(1), KeyAndRim);
        var cape = new FakeMaterial(r.Backend.Log, "you.cape");
        you.Mats = you.Mats.Append(cape).ToArray();
        var calls = you.MaterialsCalls;
        r.Svc.SetPersonLight(new EntityId(1), KeyAndRim);
        Assert.Equal(calls + 1, you.MaterialsCalls);                       // one enumeration per apply
        Assert.Equal(CountLit(you), r.Svc.PersonResidual(new EntityId(1)));  // the cape's 4 values are in the snapshot too
    }

    private static int CountLit(FakeLightModel m) =>
        m.Mats.Sum(x => x.Values.Count);   // every captured value of a fully lit model differs from its original

    // ── I-2 ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("set flag 37=True")]
    [InlineData("set value 2")]
    [InlineData("set active True")]
    [InlineData("set flag 40=False")]
    public void I2_a_raise_that_throws_part_way_rolls_back_every_write(string failing)
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var before = v.State;
        v.ThrowOn = failing;
        Assert.Throws<InvalidOperationException>(() => LightGate.Raise(v, 2f));
        v.ThrowOn = null;
        var after = v.State;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Active, after.Active);
    }

    [Fact]
    public void I2_restore_attempts_every_write_even_when_one_fails()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var before = v.State;
        var gate = LightGate.Raise(v, 2f)!;
        v.ThrowOn = "set value 1";                                          // the first write-back fails
        Assert.Throws<InvalidOperationException>(() => gate.Restore());
        v.ThrowOn = null;
        var after = v.State;
        Assert.Equal(before.Flags, after.Flags);                            // … every flag still went back
        Assert.Equal(before.Active, after.Active);                          // … and active
    }

    [Fact]
    public void I2_the_service_never_drops_a_raised_gate_without_restoring_it()
    {
        var r = new LightsRig();
        var before = r.Backend.Volume.State;
        r.Svc.PeopleLevel = 2f;
        r.Svc.AddLamp(LightsRig.Lamp());
        Assert.True(r.Svc.GateRaised);
        r.Backend.Volume.ThrowOn = "set value 5";                           // moving the level throws
        r.Svc.PeopleLevel = 5f;
        r.Backend.Volume.ThrowOn = null;
        Assert.False(r.Svc.GateRaised);
        var after = r.Backend.Volume.State;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Active, after.Active);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void I2_a_failed_raise_in_the_service_leaves_the_volume_as_the_game_had_it()
    {
        var r = new LightsRig();
        var before = r.Backend.Volume.State;
        r.Backend.Volume.ThrowOn = "set active True";
        r.Svc.PeopleLevel = 2f;
        r.Svc.AddLamp(LightsRig.Lamp());
        r.Backend.Volume.ThrowOn = null;
        Assert.False(r.Svc.GateRaised);
        var after = r.Backend.Volume.State;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Active, after.Active);
    }

    // ── I-4 ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("value")]
    [InlineData("flag")]
    [InlineData("active")]
    public void I4_a_volume_the_game_wrote_since_is_left_to_the_game(string what)
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var gate = LightGate.Raise(v, 2f)!;
        switch (what)
        {
            case "value": v.GameWrites(value: 0.7f); break;                 // e.g. a cutscene's FixedLightTrack
            case "flag": v.GameWrites(flag: 12); break;
            default: v.GameWrites(active: false); break;
        }
        var gameState = v.State;
        log.Entries.Clear();
        gate.Restore();
        Assert.True(gate.LeftToGame);
        Assert.DoesNotContain(log.Entries, e => e.StartsWith("set", StringComparison.Ordinal));
        var after = v.State;
        Assert.Equal(gameState.Flags, after.Flags);
        Assert.Equal(gameState.Value, after.Value);
        Assert.Equal(gameState.Active, after.Active);
    }

    [Fact]
    public void I4_an_untouched_volume_is_restored_and_not_left_to_the_game()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log);
        var before = v.State;
        var gate = LightGate.Raise(v, 2f)!;
        gate.SetLevel(4f);
        gate.Restore();
        Assert.False(gate.LeftToGame);
        Assert.Equal(before.Value, v.State.Value);
        Assert.Equal(before.Flags, v.State.Flags);
    }

    [Fact]
    public void I4_an_already_active_volume_keeps_the_games_overrides_and_gets_only_ours_added()
    {
        var log = new LightLog();
        var v = new FakeGateVolume(log, active: true);
        var before = v.State;
        var gate = LightGate.Raise(v, 2f)!;
        var raised = v.State;
        for (var i = 0; i < before.Flags.Length; i++)
            Assert.Equal(i == v.GateIndex || before.Flags[i], raised.Flags[i]);
        Assert.Equal(2f, raised.Value);
        Assert.True(raised.Active);
        Assert.Single(log.Entries, e => e.StartsWith("set flag", StringComparison.Ordinal));   // only the gate override
        Assert.DoesNotContain("set active True", log.Entries);
        gate.Restore();
        var after = v.State;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Value, after.Value);
        Assert.True(after.Active);
    }

    // ── Concern 1 + model liveness ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Concern1_a_posed_copy_born_lit_is_restored_to_the_originals()
    {
        var r = new LightsRig();
        var real = r.Backend.AddPerson(2, "celia");
        var original = real.Snapshot();
        r.Svc.SetPersonLight(new EntityId(2), KeyAndRim);
        var copy = new FakeLightModel(r.Backend.Log, "copy");
        for (var i = 0; i < copy.Mats.Length; i++)                         // cloned from the LIT real model
            foreach (var (k, v) in real.Mats[i].Values) copy.Mats[i].Values[k] = v;
        r.Backend.Models[2] = copy;
        r.Svc.RefreshPeople();
        r.Svc.SetPersonLight(new EntityId(2), PersonLight.None);
        LightsPeopleTests.AssertSame(original, copy.Snapshot());           // not left lit
        LightsPeopleTests.AssertSame(original, real.Snapshot());
    }

    [Fact]
    public void Concern1_a_copy_with_its_own_values_keeps_them()
    {
        var r = new LightsRig();
        r.Backend.AddPerson(2, "celia");
        r.Svc.SetPersonLight(new EntityId(2), KeyAndRim);
        var copy = new FakeLightModel(r.Backend.Log, "copy");
        foreach (var m in copy.Mats)                                        // its own look, unrelated to our light
            if (m.Values.ContainsKey(LightProperty.CameraLightParm)) m.Values[LightProperty.CameraLightParm] = new LightVector(0.9f, 0.8f, 0.7f, 0f);
        var copyOriginal = copy.Snapshot();
        r.Backend.Models[2] = copy;
        r.Svc.RefreshPeople();
        r.Svc.SetPersonLight(new EntityId(2), PersonLight.None);
        LightsPeopleTests.AssertSame(copyOriginal, copy.Snapshot());
    }

    [Fact]
    public void A_model_reported_gone_whose_materials_live_is_still_written_back()
    {
        var r = new LightsRig();
        var npc = r.Backend.AddPerson(3, "npc");
        var original = npc.Snapshot();
        r.Svc.SetPersonLight(new EntityId(3), KeyAndRim);
        npc.Live = () => false;                                             // the model wrapper is gone, its materials are not
        r.Svc.RefreshPeople();
        Assert.Equal(0, r.Svc.LitPeopleCount);
        LightsPeopleTests.AssertSame(original, npc.Snapshot());
    }
}
