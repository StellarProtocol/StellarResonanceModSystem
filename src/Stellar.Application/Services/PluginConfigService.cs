// src/Stellar.Application/Services/PluginConfigService.cs
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Per-plugin section-based JSON config cache. Reads/writes go through the
/// in-memory <see cref="JsonObject"/> tree; <see cref="IConfigSection.Save"/>
/// flushes the whole tree through <see cref="IConfigStore"/>. External edits
/// detected by the store are diffed against the cached tree — sections whose
/// content actually changed fire <see cref="SectionChanged"/>; identical
/// content (the FileSystemWatcher echo of our own write) is suppressed.
/// </summary>
internal sealed class PluginConfigService : IPluginConfig
{
    private readonly IConfigStore _store;
    private readonly string _pluginGuid;
    private readonly Dictionary<string, ConfigSection> _sections = new();
    private readonly object _lock = new();
    private JsonObject _root;

    public event Action<string>? SectionChanged;

    public PluginConfigService(IConfigStore store, string pluginGuid)
    {
        _store = store;
        _pluginGuid = pluginGuid;
        _root = LoadRoot();
        _store.ExternalFileChanged += OnExternalFileChanged;
    }

    public IConfigSection GetSection(string name)
    {
        lock (_lock)
        {
            if (!_sections.TryGetValue(name, out var section))
            {
                section = new ConfigSection(this, name);
                _sections[name] = section;
            }
            return section;
        }
    }

    /// <summary>
    /// Returns the live <see cref="JsonObject"/> backing the named section,
    /// creating it if absent. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private JsonObject GetOrCreateSectionNodeLocked(string name)
    {
        if (_root[name] is JsonObject existing)
        {
            return existing;
        }
        // Detach any non-object value first so JsonObject indexer can replace it.
        if (_root.ContainsKey(name))
        {
            _root.Remove(name);
        }
        var fresh = new JsonObject();
        _root[name] = fresh;
        return fresh;
    }

    private T? GetValue<T>(string sectionName, string key, T? defaultValue)
    {
        lock (_lock)
        {
            try
            {
                var section = GetOrCreateSectionNodeLocked(sectionName);
                if (!section.TryGetPropertyValue(key, out var node) || node is null)
                {
                    return defaultValue;
                }
                return node.Deserialize<T>();
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    private void SetValue<T>(string sectionName, string key, T value)
    {
        lock (_lock)
        {
            var section = GetOrCreateSectionNodeLocked(sectionName);
            // Detach any existing value first so the indexer can replace it cleanly.
            if (section.ContainsKey(key))
            {
                section.Remove(key);
            }
            section[key] = value is null
                ? null
                : JsonSerializer.SerializeToNode(value);
        }
    }

    private void SaveSection(string sectionName)
    {
        JsonObject clone;
        lock (_lock)
        {
            clone = CloneObject(_root);
        }
        _store.Save(_pluginGuid, clone);
        SectionChanged?.Invoke(sectionName);
    }

    private void SaveSectionQuiet(string sectionName)
    {
        JsonObject clone;
        lock (_lock)
        {
            clone = CloneObject(_root);
        }
        _store.Save(_pluginGuid, clone);
        // Intentionally does NOT fire SectionChanged — caller is reacting to its own change.
        _ = sectionName; // suppress unused-parameter warning; kept for symmetry with SaveSection
    }

    private JsonObject LoadRoot()
    {
        if (_store.TryLoad(_pluginGuid, out var node) && node is JsonObject obj)
        {
            return obj;
        }
        return new JsonObject();
    }

    private void OnExternalFileChanged(string pluginGuid)
    {
        if (pluginGuid != _pluginGuid) return;
        if (!_store.TryLoad(_pluginGuid, out var newRoot) || newRoot is not JsonObject newObj)
        {
            return;
        }

        HashSet<string> changed;
        lock (_lock)
        {
            changed = DiffSectionNames(_root, newObj);
            if (changed.Count == 0) return;  // identical content = echo
            _root = newObj;
            // Invalidate the cached ConfigSection wrappers' backing JsonObjects:
            // the next Get/Set call will look up the live root again, so we
            // don't need to drop the IConfigSection identities — they're
            // stateless façades that re-resolve via _root each call.
        }

        foreach (var name in changed)
        {
            SectionChanged?.Invoke(name);
        }
    }

    /// <summary>
    /// Returns the set of section names whose serialized content differs
    /// between <paramref name="before"/> and <paramref name="after"/>. Uses
    /// canonical-string comparison (<see cref="JsonNode.ToJsonString()"/>)
    /// to stay compatible with net6.0, which lacks JsonNode.DeepEquals.
    /// </summary>
    private static HashSet<string> DiffSectionNames(JsonObject before, JsonObject after)
    {
        var changed = new HashSet<string>();
        foreach (var kv in before)
        {
            if (!after.TryGetPropertyValue(kv.Key, out var newVal)
                || !JsonNodeEquals(kv.Value, newVal))
            {
                changed.Add(kv.Key);
            }
        }
        foreach (var kv in after)
        {
            if (!before.ContainsKey(kv.Key))
            {
                changed.Add(kv.Key);
            }
        }
        return changed;
    }

    private static bool JsonNodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.ToJsonString() == b.ToJsonString();
    }

    /// <summary>
    /// net6.0-compatible deep clone of a <see cref="JsonObject"/> via
    /// round-trip through its serialized form.
    /// </summary>
    private static JsonObject CloneObject(JsonObject src)
    {
        var parsed = JsonNode.Parse(src.ToJsonString());
        return parsed as JsonObject ?? new JsonObject();
    }

    private sealed class ConfigSection : IConfigSection
    {
        private readonly PluginConfigService _owner;
        private readonly string _name;

        public ConfigSection(PluginConfigService owner, string name)
        {
            _owner = owner;
            _name = name;
        }

        public T? Get<T>(string key, T? defaultValue)
            => _owner.GetValue(_name, key, defaultValue);

        public void Set<T>(string key, T value)
            => _owner.SetValue(_name, key, value);

        public void Save() => _owner.SaveSection(_name);
        public void SaveQuiet() => _owner.SaveSectionQuiet(_name);
    }
}
