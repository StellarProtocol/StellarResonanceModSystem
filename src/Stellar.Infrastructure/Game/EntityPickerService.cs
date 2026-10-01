using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>Picks the character under a screen point: projects the local player's, every listed character's and every NPC's chest
/// with the main camera and takes the nearest within <see cref="ScreenPick"/>'s radius. One projection per character,
/// only on a click. Main thread.</summary>
internal sealed class EntityPickerService : IEntityPicker
{
    private readonly GameEntityAccess _entities;
    private readonly Func<Camera?> _mainCamera;
    private readonly List<long> _ids = new();
    private readonly List<long> _all = new();
    private readonly List<PickCandidate> _candidates = new();

    public EntityPickerService(GameEntityAccess entities, Func<Camera?> mainCamera)
    {
        _entities = entities;
        _mainCamera = mainCamera;
    }

    public bool TryPickEntity(float screenX, float screenY, out EntityId entityId)
    {
        entityId = EntityId.None;
        var cam = _mainCamera();
        if (cam == null) return false;
        _candidates.Clear();
        Add(cam, _entities.LocalEntity());
        _entities.CharIds(_ids);
        foreach (var id in _ids) Add(cam, _entities.CharEntity(id));
        _entities.EntityUuids(_all);   // NPCs too (2.15.0 posing): one dictionary walk per click
        foreach (var each in _all)
            if (_entities.EntityByUuid(each) is { } npc && _entities.EntType(npc) == FreezeKinds.Npc) Add(cam, npc);
        if (ScreenPick.Nearest(_candidates, screenX, screenY, Screen.height, cam.fieldOfView) is not long uuid) return false;
        entityId = new EntityId(uuid);
        return true;
    }

    private void Add(Camera cam, object? entity)
    {
        if (entity is null || _entities.LiveModel(entity) is not { } model || _entities.Chest(model) is not Vector3 chest) return;
        var sp = cam.WorldToScreenPoint(chest);
        _candidates.Add(new PickCandidate(_entities.Uuid(entity), sp.x, Screen.height - sp.y, sp.z));
    }
}
