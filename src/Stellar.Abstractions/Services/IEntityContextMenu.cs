using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>A registered context-menu item, scoped to the entity it was resolved for.</summary>
public readonly record struct EntityMenuItem(string Label, Action OnClick)
{
    /// <summary>Optional icon for the item (framework 2.33.0): PNG bytes of a flat WHITE glyph on transparency — the
    /// renderer tints it (muted, or red for a destructive item) and draws it in a 16 px slot at the label's left. Supply
    /// a 32 px drawing (it reads crisply at every UI scale the menu draws at); null = the renderer's generic dot.
    /// The same array is handed back on every <see cref="IEntityContextMenu.ItemsFor"/> call, so a renderer may cache by
    /// reference. An <c>init</c> property, NOT a record-constructor parameter, so plugins compiled against an older
    /// Abstractions keep binding <c>new EntityMenuItem(label, onClick)</c>.</summary>
    public byte[]? IconPng { get; init; }
}

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

    /// <summary>
    /// Register a menu item with an icon (framework 2.33.0): as the three-argument overload, plus
    /// <paramref name="iconPng"/> = PNG bytes of a flat white glyph, surfaced as <see cref="EntityMenuItem.IconPng"/>
    /// (null = no icon, same as the three-argument overload). The default body drops the icon, so an implementor
    /// compiled before 2.33.0 keeps loading; the framework's own service overrides it.
    /// </summary>
    IDisposable Register(string label, Func<EntityId, bool>? isVisible, Action<EntityId> onClick, byte[]? iconPng)
        => Register(label, isVisible, onClick);

    /// <summary>The menu items currently visible for <paramref name="entity"/>, each pre-bound to it.</summary>
    IReadOnlyList<EntityMenuItem> ItemsFor(EntityId entity);
}
