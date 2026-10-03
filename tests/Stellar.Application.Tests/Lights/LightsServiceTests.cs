using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Hosting;
using Xunit;

namespace Stellar.Application.Tests.Lights;

// Lights spec § 4 / § 6: lamps per plugin (≤ 8 — the framework enforces the cap), the gate raised only while a lamp is on
// and the level is above 0 and restored the moment the last lamp goes off, release order people → lamps → gate.
public sealed class LightsServiceTests
{
    [Fact]
    public void Gate_rises_with_the_first_lamp_on_and_restores_exactly_when_the_last_goes_off()
    {
        var r = new LightsRig();
        var before = r.Backend.Volume.State;
        r.Svc.PeopleLevel = 2f;
        Assert.False(r.Svc.GateRaised);                       // a level alone raises nothing
        var a = r.Svc.AddLamp(LightsRig.Lamp());
        var b = r.Svc.AddLamp(LightsRig.Lamp(x: 3f));
        Assert.True(r.Svc.GateRaised);
        Assert.Equal(2f, r.Backend.Volume.State.Value);
        r.Svc.RemoveLamp(a);
        Assert.True(r.Svc.GateRaised);                        // one lamp still on
        r.Svc.UpdateLamp(b, LightsRig.Lamp(on: false));
        Assert.False(r.Svc.GateRaised);                       // the last lamp went off → restored
        var after = r.Backend.Volume.State;
        Assert.Equal(before.Flags, after.Flags);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Active, after.Active);
    }

    [Fact]
    public void Level_zero_restores_and_a_new_level_moves_the_raised_gate()
    {
        var r = new LightsRig();
        r.Svc.AddLamp(LightsRig.Lamp());
        Assert.False(r.Svc.GateRaised);                       // lamp on, level 0 → game's value
        r.Svc.PeopleLevel = 2f;
        Assert.True(r.Svc.GateRaised);
        r.Svc.PeopleLevel = 5f;
        Assert.Equal(5f, r.Backend.Volume.State.Value);
        Assert.Equal(1, r.Backend.Log.Entries.Count(e => e == "set active True"));   // moved, not re-raised
        r.Svc.PeopleLevel = 0f;
        Assert.False(r.Svc.GateRaised);
        Assert.Equal(1f, r.Backend.Volume.State.Value);
    }

    [Fact]
    public void Level_is_clamped_to_the_documented_range()
    {
        var r = new LightsRig();
        r.Svc.PeopleLevel = 999f;
        Assert.Equal(LightLimits.MaxPeopleLevel, r.Svc.PeopleLevel);
        r.Svc.PeopleLevel = -3f;
        Assert.Equal(0f, r.Svc.PeopleLevel);
    }

    [Fact]
    public void Cap_is_eight_lamps_per_plugin_and_counts_per_plugin()
    {
        var r = new LightsRig();
        var a = new PluginLights(r.Svc, new object());
        var b = new PluginLights(r.Svc, new object());
        for (var i = 0; i < LightLimits.MaxLampsPerPlugin; i++) Assert.False(a.AddLamp(LightsRig.Lamp()).IsNone);
        Assert.True(a.AddLamp(LightsRig.Lamp()).IsNone);       // the 9th is refused, nothing made
        Assert.Equal(8, r.Backend.Lamps.Count);
        Assert.False(b.AddLamp(LightsRig.Lamp()).IsNone);      // another plugin has its own 8
    }

    [Fact]
    public void A_plugin_cannot_change_or_remove_another_plugins_lamp()
    {
        var r = new LightsRig();
        var a = new PluginLights(r.Svc, new object());
        var b = new PluginLights(r.Svc, new object());
        var lamp = a.AddLamp(LightsRig.Lamp());
        Assert.False(b.UpdateLamp(lamp, LightsRig.Lamp(on: false)));
        b.RemoveLamp(lamp);
        Assert.False(r.Backend.Lamps[0].Destroyed);
        Assert.True(a.UpdateLamp(lamp, LightsRig.Lamp(on: false)));
    }

    [Fact]
    public void Nothing_is_made_while_unavailable_and_settings_are_clamped()
    {
        var r = new LightsRig { Available = false };
        Assert.True(r.Svc.AddLamp(LightsRig.Lamp()).IsNone);
        Assert.Empty(r.Backend.Lamps);
        r.Available = true;
        r.Svc.AddLamp(LightsRig.Lamp() with { Strength = 1e6f, Range = 0f, Color = new RgbColor(3f, -1f, 0.5f) });
        var s = r.Backend.Lamps[0].Settings;
        Assert.Equal(LightLimits.MaxStrength, s.Strength);
        Assert.Equal(LightLimits.MinRange, s.Range);
        Assert.Equal(new RgbColor(1f, 0f, 0.5f), s.Color);
    }

    [Fact]
    public void A_failed_lamp_is_warned_once_and_returns_none()
    {
        var r = new LightsRig();
        r.Backend.ThrowOnCreate = true;
        Assert.True(r.Svc.AddLamp(LightsRig.Lamp()).IsNone);
        Assert.True(r.Svc.AddLamp(LightsRig.Lamp()).IsNone);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Release_all_order_is_people_then_lamps_then_gate_and_raises_Released_once()
    {
        var r = new LightsRig();
        var self = r.Backend.AddPerson(1, "you");
        var original = self.Snapshot();
        r.Svc.PeopleLevel = 2f;
        r.Svc.AddLamp(LightsRig.Lamp());
        r.Svc.AddLamp(LightsRig.Lamp(x: 2f));
        r.Svc.SetPersonLight(new EntityId(1), new PersonLight(new KeyLight(-60f, 25f), new RimLight(new RgbColor(1f, 0.4f, 0.1f), 0.45f)));
        var released = 0;
        r.Svc.Released += () => released++;
        r.Backend.Log.Entries.Clear();

        r.Svc.ReleaseAll();

        var log = r.Backend.Log;
        var lastWriteBack = log.LastIndexOf("write you.");
        var firstDestroy = log.IndexOf("destroy lamp");
        var lastDestroy = log.LastIndexOf("destroy lamp");
        var gateRestore = log.IndexOf("set value 1");
        Assert.True(lastWriteBack >= 0 && lastWriteBack < firstDestroy, "people written back before any lamp goes");
        Assert.True(lastDestroy < gateRestore, "the gate is restored after the last lamp went");
        Assert.Equal("set active False", log.Entries[^1]);
        Assert.Equal(1, released);
        Assert.Equal(0, r.Svc.LampCount);
        Assert.False(r.Svc.GateRaised);
        LightsPeopleTests.AssertSame(original, self.Snapshot());
    }

    [Fact]
    public void Release_all_with_nothing_held_is_a_no_op_and_raises_nothing()
    {
        var r = new LightsRig();
        var released = 0;
        r.Svc.Released += () => released++;
        r.Svc.ReleaseAll();
        r.Svc.AddLamp(LightsRig.Lamp());
        r.Svc.ReleaseAll();
        r.Svc.ReleaseAll();   // the SceneChanged backstop after the leave prefix
        Assert.Equal(1, released);
    }

    [Fact]
    public void Plugin_unload_ends_only_its_lights_and_drops_its_Released_handlers()
    {
        var r = new LightsRig();
        var a = new PluginLights(r.Svc, new object());
        var b = new PluginLights(r.Svc, new object());
        a.PeopleLevel = 2f;
        a.AddLamp(LightsRig.Lamp());
        b.AddLamp(LightsRig.Lamp());
        var aReleased = 0;
        a.Released += () => aReleased++;
        a.ReleaseAll();
        Assert.True(r.Backend.Lamps[0].Destroyed);
        Assert.False(r.Backend.Lamps[1].Destroyed);
        Assert.False(r.Svc.GateRaised);                       // b's lamp alone: b's level is 0
        Assert.Equal(0f, a.PeopleLevel);
        r.Svc.ReleaseAll();
        Assert.Equal(0, aReleased);
    }

    [Fact]
    public void Another_plugins_level_does_not_light_people_through_my_lamps()
    {
        var r = new LightsRig();
        var a = new PluginLights(r.Svc, new object());
        var b = new PluginLights(r.Svc, new object());
        a.AddLamp(LightsRig.Lamp());
        b.PeopleLevel = 5f;                                     // b has no lamp
        Assert.False(r.Svc.GateRaised);
    }

    [Fact]
    public void No_weather_volume_is_warned_once_and_nothing_is_raised()
    {
        var r = new LightsRig();
        r.Backend.NoVolume = true;
        r.Svc.PeopleLevel = 2f;
        r.Svc.AddLamp(LightsRig.Lamp());
        r.Svc.AddLamp(LightsRig.Lamp());
        Assert.False(r.Svc.GateRaised);
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void Lamp_ids_are_never_reused()
    {
        var r = new LightsRig();
        var ids = new HashSet<int>();
        for (var i = 0; i < 20; i++)
        {
            var id = r.Svc.AddLamp(LightsRig.Lamp());
            Assert.True(ids.Add(id.Value));
            r.Svc.RemoveLamp(id);
        }
        Assert.False(r.Svc.UpdateLamp(new LampId(1), LightsRig.Lamp()));
    }
}
