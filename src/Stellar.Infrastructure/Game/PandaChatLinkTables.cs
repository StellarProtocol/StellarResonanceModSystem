using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IChatLinkTables"/> over the game's static config tables: <c>Bokura.ChatHyperlinkBase</c> (chat link
/// templates), <c>Bokura.UnionBuildingTableBase</c> and <c>Bokura.MessageTableBase</c> (tip texts). Same access pattern
/// as the sticker table (<c>GameAssetsService.Reflection.cs</c>): <c>GetTable(true)</c> (AutoLoad REQUIRED) →
/// <c>TryGetValue(id, out row, errorWhenNotFound: false)</c> (the default TRUE logs every miss to the game log) →
/// row PROPERTY getters (Il2CppInterop surfaces IL2CPP fields as properties). Every result — hit or miss — is cached
/// per id once the table itself resolved, so each id costs one reflective lookup per session; a not-yet-loaded table
/// caches nothing and is retried on the next call. The string columns are plain <c>string</c>s already in the client
/// language (no MLString). Main thread only (Drain / share calls).
/// </summary>
internal sealed class PandaChatLinkTables : IChatLinkTables
{
    private const int CacheCap = 4096;   // ids are bounded by the table sizes; the cap only guards pathological input

    private readonly IPluginLog _log;
    private readonly KeyedTable _hyperlink = new("Bokura.ChatHyperlinkBase");
    private readonly KeyedTable _union = new("Bokura.UnionBuildingTableBase");
    private readonly KeyedTable _message = new("Bokura.MessageTableBase");
    private readonly Dictionary<int, ChatHyperlinkRow?> _hyperlinkCache = new();
    private readonly Dictionary<int, string?> _unionCache = new();
    private readonly Dictionary<int, string?> _messageCache = new();

    public PandaChatLinkTables(IPluginLog log) => _log = log;

    public ChatHyperlinkRow? GetHyperlink(int configId)
    {
        if (_hyperlinkCache.TryGetValue(configId, out var cached)) return cached;
        if (!_hyperlink.TryGetRow(configId, out var row, _log)) return null;   // table not ready: don't cache
        var value = row is null ? null : new ChatHyperlinkRow(
            configId,
            _hyperlink.ReadString(row, "Content"),
            _hyperlink.ReadInt(row, "FuncType"),
            _hyperlink.ReadString(row, "FunctionButtonEscape"));
        Store(_hyperlinkCache, configId, value);
        return value;
    }

    public string? GetUnionBuildingName(int buildingId) => CachedString(_union, _unionCache, buildingId, "BuildingName");

    public string? GetMessageText(int messageId) => CachedString(_message, _messageCache, messageId, "Content");

    private string? CachedString(KeyedTable table, Dictionary<int, string?> cache, int id, string column)
    {
        if (cache.TryGetValue(id, out var cached)) return cached;
        if (!table.TryGetRow(id, out var row, _log)) return null;
        var s = row is null ? null : table.ReadString(row, column);
        Store(cache, id, string.IsNullOrEmpty(s) ? null : s);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static void Store<T>(Dictionary<int, T> cache, int id, T value)
    {
        if (cache.Count >= CacheCap) cache.Clear();
        cache[id] = value;
    }

    /// <summary>One <c>ZTable&lt;int, Row&gt;</c>: type + method handles resolved once, row getters cached by name.</summary>
    private sealed class KeyedTable
    {
        private readonly string _typeName;
        private Type? _rowType;
        private MethodInfo? _getTable, _tryGetValue;
        private readonly Dictionary<string, MethodInfo?> _getters = new(StringComparer.Ordinal);
        private bool _failLogged;

        public KeyedTable(string typeName) => _typeName = typeName;

        /// <summary>True when the table answered (row = the row, or null for an unknown id); false when the table
        /// type/instance isn't available yet (caller must not cache).</summary>
        public bool TryGetRow(int id, out object? row, IPluginLog log)
        {
            row = null;
            _rowType ??= StellarInterop.FindType(_typeName);
            _getTable ??= StellarInterop.FindMethod(_rowType, "GetTable", 1);
            if (_getTable is null) return false;
            try
            {
                var table = _getTable.Invoke(null, new object[] { true });   // AutoLoad=true REQUIRED
                if (table is null) return false;
                _tryGetValue ??= StellarInterop.FindMethod(table.GetType(), "TryGetValue", 3);
                if (_tryGetValue is null) return false;
                var args = new object?[] { id, null, false };                 // (key, out row, errorWhenNotFound: false)
                if (_tryGetValue.Invoke(table, args) is true) row = args[1];
                return true;
            }
            catch (Exception ex)
            {
                if (!_failLogged)
                {
                    _failLogged = true;
                    log.Warning($"[Chat][links] {_typeName} lookup threw for id={id}: {ex.GetType().Name}: {ex.Message}");
                }
                return false;
            }
        }

        public string ReadString(object row, string column)
        {
            try { return Getter(column)?.Invoke(row, null) as string ?? string.Empty; }
            catch { return string.Empty; }
        }

        public int ReadInt(object row, string column)
        {
            try { return Getter(column)?.Invoke(row, null) is int i ? i : 0; }
            catch { return 0; }
        }

        private MethodInfo? Getter(string column)
        {
            if (_getters.TryGetValue(column, out var g)) return g;
            g = StellarInterop.FindPropertyUp(_rowType, column)?.GetGetMethod(nonPublic: true);
            _getters[column] = g;
            return g;
        }
    }
}
