using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Lights;

// Lights spec § 4 / § 7, recon Run 13 Q3: per-person key (_CameraLightParm via SetFixedLight) and rim (SetFresnelEffect).
// The material values are snapshotted BEFORE the first write and written back exactly (the game's own Fresnel "off" leaves
// colour + params); a person who moves to another model (posed / reset) is restored on the old one and re-lit on the new.
public sealed class LightsPeopleTests
{
    private static readonly PersonLight KeyAndRim =
        new(new KeyLight(-60f, 25f), new RimLight(new RgbColor(1f, 0.45f, 0.15f), 0.45f));

    internal static void AssertSame(List<Dictionary<LightProperty, LightVector>> a, List<Dictionary<LightProperty, LightVector>> b)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Count, b[i].Count);
            foreach (var (k, v) in a[i]) Assert.Equal(v, b[i][k]);
        }
    }

    [Fact]
    public void The_snapshot_is_read_before_the_first_write()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        var original = you.Snapshot();
        Assert.True(r.Svc.SetPersonLight(new EntityId(1), KeyAndRim));
        Assert.Equal(1, you.MaterialsCalls);
        Assert.True(r.Svc.PersonResidual(new EntityId(1)) > 0);   // the model changed after the snapshot
        // the snapshot holds the ORIGINAL values: restoring proves it
        r.Svc.SetPersonLight(new EntityId(1), PersonLight.None);
        AssertSame(original, you.Snapshot());
    }

    [Fact]
    public void Key_and_rim_change_the_model_and_None_restores_every_value_exactly()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        var original = you.Snapshot();
        r.Svc.SetPersonLight(new EntityId(1), KeyAndRim);
        var lit = you.Snapshot();
        Assert.NotEqual(original[0][LightProperty.CameraLightParm], lit[0][LightProperty.CameraLightParm]);
        Assert.Equal(1f, lit[1][LightProperty.UseFresnel].X);
        Assert.True(r.Svc.SetPersonLight(new EntityId(1), PersonLight.None));
        AssertSame(original, you.Snapshot());
        Assert.Equal(-1, r.Svc.PersonResidual(new EntityId(1)));   // forgotten
        Assert.Equal(0, r.Svc.LitPeopleCount);
    }

    [Fact]
    public void Turning_the_key_off_writes_back_only_the_key()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        var original = you.Snapshot();
        r.Svc.SetPersonLight(new EntityId(1), KeyAndRim);
        r.Backend.Log.Entries.Clear();
        r.Svc.SetPersonLight(new EntityId(1), KeyAndRim with { Key = null });
        var now = you.Snapshot();
        Assert.Equal(original[0][LightProperty.CameraLightParm], now[0][LightProperty.CameraLightParm]);
        Assert.Equal(1f, now[1][LightProperty.UseFresnel].X);                          // the rim stays
        Assert.All(r.Backend.Log.Entries.Where(e => e.StartsWith("write", System.StringComparison.Ordinal)),
            e => Assert.EndsWith("CameraLightParm", e));
    }

    [Fact]
    public void A_rim_of_strength_zero_is_no_rim()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        var original = you.Snapshot();
        r.Svc.SetPersonLight(new EntityId(1), new PersonLight(null, new RimLight(new RgbColor(1f, 1f, 1f), 0f)));
        AssertSame(original, you.Snapshot());
        Assert.DoesNotContain(r.Backend.Log.Entries, e => e.StartsWith("rim", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Posing_moves_the_light_to_the_copy_and_restores_the_real_model()
    {
        var r = new LightsRig();
        var real = r.Backend.AddPerson(2, "celia");
        var original = real.Snapshot();
        r.Svc.SetPersonLight(new EntityId(2), KeyAndRim);
        var copy = new FakeLightModel(r.Backend.Log, "copy");
        var copyOriginal = copy.Snapshot();
        r.Backend.Models[2] = copy;              // posed: the copy is what is seen now
        r.Svc.RefreshPeople();                   // IPosing.Changed
        AssertSame(original, real.Snapshot());   // the hidden real player is back to normal
        Assert.Contains("key copy", r.Backend.Log.Entries);
        r.Svc.SetPersonLight(new EntityId(2), PersonLight.None);
        AssertSame(copyOriginal, copy.Snapshot());
    }

    [Fact]
    public void A_person_who_leaves_is_dropped_and_a_dead_model_is_never_written()
    {
        var r = new LightsRig();
        var npc = r.Backend.AddPerson(3, "npc");
        r.Svc.SetPersonLight(new EntityId(3), KeyAndRim);
        npc.Live = () => false;
        foreach (var m in npc.Mats) m.Live = false;   // a destroyed model takes its material instances with it
        r.Backend.Log.Entries.Clear();
        r.Svc.RefreshPeople();
        Assert.Equal(0, r.Svc.LitPeopleCount);
        Assert.Empty(r.Backend.Log.Entries);
        Assert.False(r.Svc.SetPersonLight(new EntityId(3), KeyAndRim));   // gone: refused
    }

    [Fact]
    public void One_owner_per_person_and_unload_restores_only_its_people()
    {
        var r = new LightsRig();
        var you = r.Backend.AddPerson(1, "you");
        var celia = r.Backend.AddPerson(2, "celia");
        var youOriginal = you.Snapshot();
        var a = new PluginLights(r.Svc, new object());
        var b = new PluginLights(r.Svc, new object());
        Assert.True(a.SetPersonLight(new EntityId(1), KeyAndRim));
        Assert.False(b.SetPersonLight(new EntityId(1), KeyAndRim));
        Assert.True(b.SetPersonLight(new EntityId(2), KeyAndRim));
        var celiaLit = celia.Snapshot();
        a.ReleaseAll();
        AssertSame(youOriginal, you.Snapshot());
        AssertSame(celiaLit, celia.Snapshot());
        Assert.Equal(1, r.Svc.LitPeopleCount);
    }

    [Fact]
    public void Release_writes_back_the_most_recently_lit_person_first()
    {
        var r = new LightsRig();
        r.Backend.AddPerson(1, "you");
        r.Backend.AddPerson(2, "celia");
        r.Svc.SetPersonLight(new EntityId(1), KeyAndRim);
        r.Svc.SetPersonLight(new EntityId(2), KeyAndRim);
        r.Backend.Log.Entries.Clear();
        r.Svc.ReleaseAll();
        Assert.True(r.Backend.Log.LastIndexOf("write celia.") < r.Backend.Log.IndexOf("write you."));
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 0f, -1f)]      // from the camera (measured (0,0,-1,1))
    [InlineData(90f, 0f, 1f, 0f, 0f)]      // from the camera's right (measured (1,0,0,1))
    [InlineData(-90f, 0f, -1f, 0f, 0f)]
    [InlineData(0f, 90f, 0f, 1f, 0f)]      // clamped to 89° — nearly straight above
    public void Key_direction_maps_to_camera_space(float dir, float height, float x, float y, float z)
    {
        var v = PersonLightMath.ToCameraSpace(new KeyLight(dir, height));
        Assert.Equal(x, v.X, 1);
        Assert.Equal(y, v.Y, 1);
        Assert.Equal(z, v.Z, 1);
        Assert.Equal(1f, v.W);
    }

    [Fact]
    public void Rim_colour_is_scaled_by_strength_and_clamped()
    {
        var c = PersonLightMath.RimColor(new RimLight(new RgbColor(1f, 0.5f, 2f), 9f));
        Assert.Equal(new LightVector(2f, 1f, 2f, 1f), c);
    }
}
