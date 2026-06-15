using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>A registered context-menu item, scoped to the entity it was resolved for.</summary>
public readonly record struct EntityMenuItem(string Label, Action OnClick);

/// <summary>
/// Extension point for entity-scoped row context menus (CombatMeter renders these on a right-click).
/// Any plugin registers items; the renderer need not know the registrant. Items can gate per-entity.
/// </summary>
public interface IEntityContextMenu
{
    /// <summary>
    /// Register a menu item. <paramref name="isVisible"/> gates per-entity (null = always visible);
    /// <paramref name="onClick"/> fires with the row's entity. Dispose the return value to unregister.
    /// </summary>
    IDisposable Register(string label, Func<EntityId, bool>? isVisible, Action<EntityId> onClick);

    /// <summary>The menu items currently visible for <paramref name="entity"/>, each pre-bound to it.</summary>
    IReadOnlyList<EntityMenuItem> ItemsFor(EntityId entity);
}
