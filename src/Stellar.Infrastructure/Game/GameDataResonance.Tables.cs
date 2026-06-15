using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

internal sealed partial class GameDataResonance
{
    // ===== Table access ===================================================

    // Resolve a Bokura.*TableBase type and call its static GetTable(autoLoad: true),
    // mirroring PandaGameDataProbe.TryGetTable. Returns null on any failure.
    private object? FetchTable(string typeName)
    {
        var tableType = _typeRegistry.FindType(typeName);
        if (tableType is null) return null;
        try
        {
            var getTable = tableType.GetMethod("GetTable", AnyStatic);
            if (getTable is null) return null;
            var parameters = getTable.GetParameters();
            return parameters.Length == 0
                ? getTable.Invoke(null, Array.Empty<object>())
                : getTable.Invoke(null, AutoLoadTrueArgs);
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][Resonance] GetTable threw for {typeName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Fetch a single row by id via the table's GetRow(id) (the game's own accessor;
    // see env_vm.lua) with a get_Item(id) indexer fallback. Null on miss.
    private object? GetRow(string typeName, int id)
    {
        var table = FetchTable(typeName);
        if (table is null) return null;

        var keyArg = new object[] { id };
        try
        {
            var getRow = table.GetType().GetMethod("GetRow", AnyInstance, binder: null, types: new[] { typeof(int) }, modifiers: null);
            if (getRow is not null) return getRow.Invoke(table, keyArg);

            var indexer = table.GetType().GetMethod("get_Item", AnyInstance, binder: null, types: new[] { typeof(int) }, modifiers: null);
            return indexer?.Invoke(table, keyArg);
        }
        catch
        {
            return null;
        }
    }
}
