using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Null-object implementations of <see cref="IEntityDetail"/> and
/// <see cref="IEntityContextMenu"/>. Returned from the Host wiring until Task 5
/// wires in the real Infrastructure adapters.
/// </summary>
internal sealed class NullEntityDetail : IEntityDetail
{
    public static readonly NullEntityDetail Instance = new();
    private static readonly IReadOnlyDictionary<int, long> _emptyAttrs =
        new System.Collections.ObjectModel.ReadOnlyDictionary<int, long>(new Dictionary<int, long>());
    public IReadOnlyDictionary<int, long> GetAttributes(EntityId entity) => _emptyAttrs;
    public IReadOnlyList<EquippedItem> GetEquipment(EntityId entity) =>
        Array.Empty<EquippedItem>();
    public IReadOnlyList<FashionEntry> GetFashion(EntityId entity) =>
        Array.Empty<FashionEntry>();
    public SocialSnapshot? GetSocialSnapshot(EntityId entity) => null;
    public void RefreshSocialSnapshot(EntityId entity) { }
}

internal sealed class NullEntityPortrait : IEntityPortrait
{
    public static readonly NullEntityPortrait Instance = new();
    public bool IsActive => false;
    public void Show(EntityId entity) { }
    public void Hide() { }
    public object? Texture => null;
    public void Orbit(float dx, float dy) { }
    public void Zoom(float delta) { }
    public void Pan(float dx, float dy) { }
    public void SetViewport(int width, int height) { }
}

internal sealed class NullEntityContextMenu : IEntityContextMenu
{
    public static readonly NullEntityContextMenu Instance = new();
    public IDisposable Register(string label, Func<EntityId, bool>? isVisible, Action<EntityId> onClick) =>
        NullDisposable.Instance;
    public IReadOnlyList<EntityMenuItem> ItemsFor(EntityId entity) =>
        Array.Empty<EntityMenuItem>();

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
