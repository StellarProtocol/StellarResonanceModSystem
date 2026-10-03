using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// Key light and rim per person (recon Run 13 Q3). A person is lit on the model they are seen as — their posed copy or NPC
/// stand-in while there is one, else their own model (<see cref="ILightsBackend.ResolveModel"/>). The model's material
/// values are snapshotted before the first write (<see cref="ModelLightSnapshot"/>) and written back on
/// <see cref="PersonLight.None"/>, on release, or when the person moves to another model (posed / reset — re-checked on
/// every <c>IPosing.Changed</c>), where the light is then applied afresh. A person who is gone is dropped; a dead model is
/// never written. One owner per person. Main thread.
/// </summary>
internal sealed partial class LightsService
{
    private readonly Dictionary<long, Person> _people = new();

    private sealed class Person
    {
        public Person(object owner) => Owner = owner;
        public object Owner { get; }
        public PersonLight Light { get; set; }
        public ILightModel? Model { get; set; }
        public ModelLightSnapshot? Snapshot { get; set; }
        public bool KeyOn { get; set; }
        public bool RimOn { get; set; }
    }

    /// <summary>People with a key light or rim (all owners).</summary>
    internal int LitPeopleCount => _people.Count;

    /// <summary>The material values of <paramref name="person"/>'s lit model that differ from the snapshot (readback); −1
    /// when not lit.</summary>
    internal int PersonResidual(EntityId person) =>
        _people.TryGetValue(person.Value, out var p) && p.Snapshot is { } s ? s.Residual() : -1;

    internal bool SetPersonLight(object owner, EntityId person, PersonLight light)
    {
        if (!IsAvailable || person.IsNone) return false;
        _people.TryGetValue(person.Value, out var p);
        if (p is not null && !ReferenceEquals(p.Owner, owner)) return false;
        if (light.IsNone)
        {
            if (p is not null) Drop(person.Value, p);
            return true;
        }
        ILightModel? model;
        try { model = _backend.ResolveModel(person.Value); }
        catch (Exception ex)
        {
            WarnOnce("resolve", "lights: a person's model could not be read: " + (ex.InnerException ?? ex).Message);
            model = null;
        }
        if (model is null)
        {
            if (p is not null) Drop(person.Value, p);
            return false;
        }
        p ??= new Person(owner);
        _people[person.Value] = p;
        p.Light = light;
        return Apply(person.Value, p, model);
    }

    /// <summary>A person was posed or reset (<c>IPosing.Changed</c>): each lit person follows the model now shown, and a
    /// person who is gone is dropped.</summary>
    internal void RefreshPeople()
    {
        if (_people.Count == 0) return;
        foreach (var (uuid, p) in _people.ToList())
        {
            ILightModel? model = null;
            try { model = _backend.ResolveModel(uuid); }
            catch (Exception ex) { WarnOnce("resolve", "lights: a person's model could not be read: " + (ex.InnerException ?? ex).Message); }
            if (model is null) Drop(uuid, p);
            else if (p.Model is null || !p.Model.IsSame(model)) Apply(uuid, p, model);
        }
    }

    private bool Apply(long uuid, Person p, ILightModel model)
    {
        try
        {
            if (p.Model is { } old && !old.IsSame(model)) RestoreModel(uuid, p);
            if (p.Model is null || p.Snapshot is null)
            {
                p.Model = model;
                p.Snapshot = ModelLightSnapshot.Capture(model.Materials());
                p.KeyOn = p.RimOn = false;
            }
            ApplyKey(uuid, p, model);
            ApplyRim(uuid, p, model);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("person-apply", "lights: a person's light could not be set: " + (ex.InnerException ?? ex).Message);
            return false;
        }
    }

    private void ApplyKey(long uuid, Person p, ILightModel model)
    {
        if (p.Light.Key is { } key)
        {
            model.ApplyKey(PersonLightMath.ToCameraSpace(key));
            p.KeyOn = true;
            OnPersonWritten(uuid, "key", p.Snapshot!.Count);
        }
        else if (p.KeyOn)
        {
            p.KeyOn = false;
            OnPersonRestored(uuid, "key", p.Snapshot!.Restore(ModelLightSnapshot.KeyProperties));
        }
    }

    private void ApplyRim(long uuid, Person p, ILightModel model)
    {
        if (PersonLightMath.Shows(p.Light.Rim))
        {
            model.ApplyRim(PersonLightMath.RimColor(p.Light.Rim!.Value), PersonLightMath.RimParms);
            p.RimOn = true;
            OnPersonWritten(uuid, "rim", p.Snapshot!.Count);
        }
        else if (p.RimOn)
        {
            p.RimOn = false;
            OnPersonRestored(uuid, "rim", p.Snapshot!.Restore(ModelLightSnapshot.RimProperties));
        }
    }

    /// <summary>Writes the person's model back (when it still lives) and forgets the model.</summary>
    private void RestoreModel(long uuid, Person p)
    {
        var model = p.Model;
        var snapshot = p.Snapshot;
        p.Model = null;
        p.Snapshot = null;
        p.KeyOn = p.RimOn = false;
        if (model is null || snapshot is null || !model.IsLive) return;
        OnPersonRestored(uuid, "all", snapshot.Restore(ModelLightSnapshot.KeyProperties.Concat(ModelLightSnapshot.RimProperties).ToArray()));
    }

    private void Drop(long uuid, Person p)
    {
        _people.Remove(uuid);
        Step("person restore", () => RestoreModel(uuid, p));
    }

    /// <summary>Restores the matching people, most recently lit first.</summary>
    private void ReleasePeople(Func<Person, bool> match)
    {
        foreach (var (uuid, p) in _people.Where(kv => match(kv.Value)).Reverse().ToList()) Drop(uuid, p);
    }
}
