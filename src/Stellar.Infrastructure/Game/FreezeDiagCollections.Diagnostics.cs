using System;
using Stellar.Abstractions.Services;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>Which game entity collections hold a uuid (diagnostics only; owner evidence 2026-10-02: frozen combat
/// monsters never froze at all, so the first question is whether the freeze's entity list — <c>ZEntityMgr.EntityDict</c>
/// — even contains them). Cross-checks every entity collection the release_3.7 interop exposes (ilspycmd):
/// <c>ZEntityMgr.EntityDict</c> (E), <c>MonsterDict</c> (M), <c>BossDict</c> (B), <c>delayRemoveEntityDict_</c> (D — an
/// entity removed with a delay, e.g. a death), and <c>ZClientEntityMgr.clientEntityDic_</c> (C — client-created
/// entities). Re-fetches each dictionary on every call (never cached across frames); member handles are cached per
/// dictionary type. Main thread.</summary>
internal sealed class FreezeDiagCollections
{
    internal const string ClientManagerType = "Panda.ZGame.ZClientEntityMgr";

    /// <summary>The collections, in flag order.</summary>
    internal enum Coll { Entity, Monster, Boss, DelayRemove, Client, Count }

    private static readonly string[] Letters = { "E", "M", "B", "D", "C" };
    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly SingletonAccess _client = new();
    private readonly object?[] _dicts = new object?[(int)Coll.Count];
    private readonly Dictionary<Type, (MethodInfo? Contains, PropertyInfo? Count, PropertyInfo? Keys)> _members = new();
    private readonly object[] _arg = new object[1];
    private PropertyInfo?[]? _mgrProps;
    private PropertyInfo? _clientDict;
    private MethodInfo? _tryDelayRemoved;

    public FreezeDiagCollections(IGameTypeRegistry types, GameEntityAccess entities)
    {
        _types = types;
        _entities = entities;
    }

    /// <summary>Re-fetches every collection for this frame's reads.</summary>
    public void Refresh()
    {
        Array.Clear(_dicts, 0, _dicts.Length);
        if (_entities.Manager() is not { } mgr || !ResolveProps(mgr.GetType())) return;
        for (var i = 0; i < (int)Coll.Client; i++) _dicts[i] = Safe(_mgrProps![i], mgr);
        if (_client.Get() is { } client) _dicts[(int)Coll.Client] = Safe(_clientDict, client);
    }

    /// <summary>Entries in <paramref name="c"/> (−1 = not readable on this client).</summary>
    public int Count(Coll c) => _dicts[(int)c] is { } d && Members(d).Count is { } p && Safe(p, d) is { } n ? Convert.ToInt32(n) : -1;

    public bool Contains(Coll c, long uuid)
    {
        if (_dicts[(int)c] is not { } d || Members(d).Contains is not { } m) return false;
        _arg[0] = uuid;
        try { return m.Invoke(d, _arg) is true; }
        catch { return false; }
    }

    /// <summary>"E,M,B" — the collections holding <paramref name="uuid"/> ("-" for none).</summary>
    public string Flags(long uuid)
    {
        var s = "";
        for (var i = 0; i < (int)Coll.Count; i++)
            if (Contains((Coll)i, uuid)) s += s.Length == 0 ? Letters[i] : "," + Letters[i];
        return s.Length == 0 ? "-" : s;
    }

    /// <summary>Appends up to <paramref name="cap"/> keys of <paramref name="c"/> to <paramref name="into"/>.</summary>
    public void Keys(Coll c, List<long> into, int cap)
    {
        if (_dicts[(int)c] is not { } d || Members(d).Keys is not { } kp) return;
        try
        {
            var keys = kp.GetValue(d);
            var en = keys?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(keys, null);
            if (en is null) return;
            var move = en.GetType().GetMethod("MoveNext")!;
            var current = en.GetType().GetProperty("Current")!;
            for (var n = 0; n < cap && move.Invoke(en, null) is true; n++) into.Add(Convert.ToInt64(current.GetValue(en)));
        }
        catch { /* the dictionary changed under us: what was read so far stands */ }
    }

    /// <summary>A delay-removed entity (<c>ZEntityMgr.TryGetDelayRemovedEntity</c> — a death still playing out, no longer
    /// served by <c>GetEntity</c>), live-checked; null when none.</summary>
    public object? DelayRemoved(long uuid)
    {
        if (_tryDelayRemoved is null || _entities.Manager() is not { } mgr) return null;
        var args = new object?[] { uuid, null };
        try { return _tryDelayRemoved.Invoke(mgr, args) is true ? _entities.Live(args[1]) : null; }
        catch { return null; }
    }

    private bool ResolveProps(Type mgr)
    {
        if (_mgrProps is not null) return true;
        _tryDelayRemoved = StellarInterop.FindMethod(mgr, "TryGetDelayRemovedEntity", 2);
        _mgrProps = new[]
        {
            StellarInterop.FindPropertyUp(mgr, "EntityDict"), StellarInterop.FindPropertyUp(mgr, "MonsterDict"),
            StellarInterop.FindPropertyUp(mgr, "BossDict"), StellarInterop.FindPropertyUp(mgr, "delayRemoveEntityDict_"),
        };
        var client = _types.FindType(ClientManagerType);
        if (_client.Resolve(client)) _clientDict = StellarInterop.FindPropertyUp(client, "clientEntityDic_");
        return true;
    }

    private (MethodInfo? Contains, PropertyInfo? Count, PropertyInfo? Keys) Members(object dict)
    {
        var t = dict.GetType();
        if (_members.TryGetValue(t, out var m)) return m;
        m = (t.GetMethod("ContainsKey", new[] { typeof(long) }), StellarInterop.FindPropertyUp(t, "Count"), StellarInterop.FindPropertyUp(t, "Keys"));
        _members[t] = m;
        return m;
    }

    private static object? Safe(PropertyInfo? p, object target)
    {
        if (p is null) return null;
        try { return p.GetValue(target); }
        catch { return null; }
    }
}
