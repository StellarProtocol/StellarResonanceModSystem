using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Application-layer implementation of <see cref="IEntityContextMenu"/>. Plugins register
/// labelled menu items with optional per-entity visibility gates; the CombatMeter renderer
/// calls <see cref="ItemsFor"/> to build the right-click menu for a row entity.
/// Thread-safe: all mutations are guarded by a single lock.
/// </summary>
internal sealed class EntityContextMenuService : IEntityContextMenu
{
    private sealed record Entry(string Label, Func<EntityId, bool>? IsVisible, Action<EntityId> OnClick, byte[]? IconPng);

    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public IDisposable Register(string label, Func<EntityId, bool>? isVisible, Action<EntityId> onClick)
        => Register(label, isVisible, onClick, iconPng: null);

    /// <inheritdoc/>
    public IDisposable Register(string label, Func<EntityId, bool>? isVisible, Action<EntityId> onClick, byte[]? iconPng)
    {
        if (onClick is null) throw new ArgumentNullException(nameof(onClick));
        var entry = new Entry(label, isVisible, onClick, iconPng);
        lock (_lock) _entries.Add(entry);
        return new Token(this, entry);
    }

    /// <inheritdoc/>
    public IReadOnlyList<EntityMenuItem> ItemsFor(EntityId entity)
    {
        var result = new List<EntityMenuItem>();
        lock (_lock)
        {
            foreach (var e in _entries)
            {
                bool visible;
                try { visible = e.IsVisible?.Invoke(entity) ?? true; }
                catch { visible = false; }

                if (visible)
                {
                    var captured = e;
                    // The registered array itself (never a copy), so a renderer can cache its texture by reference.
                    result.Add(new EntityMenuItem(captured.Label, () => captured.OnClick(entity)) { IconPng = captured.IconPng });
                }
            }
        }
        return result;
    }

    private void Remove(Entry entry)
    {
        lock (_lock) _entries.Remove(entry);
    }

    private sealed class Token : IDisposable
    {
        private readonly EntityContextMenuService _svc;
        private readonly Entry _entry;
        private bool _disposed;

        public Token(EntityContextMenuService svc, Entry entry)
        {
            _svc = svc;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _svc.Remove(_entry);
        }
    }
}
