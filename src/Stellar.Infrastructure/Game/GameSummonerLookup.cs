using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>"Ask the game who owns it" (Photo Studio Task 10): the owner of an entity whose summoner was never announced
/// on the wire (no <c>EntitySummonAppeared</c> — pets with markers 1024 / 192), read from the entity itself:
/// <c>ZEntityMgr.GetEntity(uuid)</c> → <c>ZEntity.TopSummonUuid</c> (the game's typed view of <c>AttrTopSummonerId</c>,
/// 91), falling back to <c>ZEntity.SummonUuid</c> (<c>AttrSummonerId</c>, 90). Both are the game's own <c>Int64</c>
/// getters, so no <c>TryGetAttr&lt;T&gt;</c> is ever called with a guessed T (the "arr type err" storm class).
/// <para>Event-driven: called only at classification time (effect sweep / creation hooks), never polled. Memo + TTL
/// policy is the pure <see cref="SummonerMemo"/> (unit-tested); <see cref="Clear"/> runs on scene change. A
/// client-wide "not ready" (types / manager missing) caches nothing for any uuid. Liveness:
/// the game's null-returning <c>GetEntity</c> plus <c>IsDestroying</c>. Fails open (0 + one warning). Main thread only —
/// any other thread gets 0.</para></summary>
internal sealed partial class GameSummonerLookup
{
    private const string Tag = "[PhotoStudio] ";
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private readonly SingletonAccess _manager = new();
    private readonly SummonerMemo _memo = new();
    private readonly Func<long, SummonerRead?> _read;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly NegativeProbeCache _resolveMiss = new(() => Environment.TickCount64, SummonerMemo.NegativeTtlMs);
    private readonly int _mainThread = Environment.CurrentManagedThreadId;   // constructed in Load() on the main thread
    private Func<object, long, object?>? _getEntity;
    private Func<object, bool>? _destroying;
    private Func<object, long>? _topSummonUuid, _summonUuid;

    public GameSummonerLookup(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types;
        _log = log;
        _read = Read;
    }

    /// <summary>The entity id that owns <paramref name="uuid"/> (top summoner, else direct summoner), or 0 when the game
    /// does not know / the entity is gone / anything failed. Never returns <paramref name="uuid"/> itself.</summary>
    public long TopSummonerOf(long uuid)
    {
        if (Environment.CurrentManagedThreadId != _mainThread) return 0;
        var owner = _memo.Lookup(uuid, Environment.TickCount64, _read, out var fresh);
        if (fresh is { } r) OnLookedUp(uuid, r, owner);
        return owner;
    }

    /// <summary>Forgets every remembered answer (scene change: entity ids and their summoners are scene-scoped).</summary>
    public void Clear()
    {
        if (Environment.CurrentManagedThreadId == _mainThread) _memo.Clear();
    }

    // null = not ready client-wide (nothing gets cached); otherwise what the entity reported (0s when gone / failed).
    private SummonerRead? Read(long uuid)
    {
        if (Manager() is not { } mgr) return null;
        try
        {
            if (_getEntity!(mgr, uuid) is not { } entity || _destroying!(entity)) return new SummonerRead(0, 0);
            return new SummonerRead(_topSummonUuid!(entity), _summonUuid!(entity));
        }
        catch (Exception ex)
        {
            WarnOnce("read", "effect owner lookup failed: " + (ex.InnerException ?? ex).Message);
            return new SummonerRead(0, 0);
        }
    }

    private object? Manager()
    {
        if (_resolveMiss.IsSuppressed) return null;
        if (!Resolve())
        {
            if (_resolveMiss.MarkNegative()) WarnOnce("resolve", "effect owner lookup unavailable: ZEntityMgr / ZEntity members not found");
            return null;
        }
        _resolveMiss.MarkRecovered();
        return _manager.Get();
    }

    private bool Resolve()
    {
        if (_summonUuid is not null) return true;
        var mgr = _types.FindType(GameEntityAccess.ManagerType);
        var ent = _types.FindType(GameEntityAccess.EntityType);
        if (mgr is null || ent is null || !_manager.Resolve(mgr)) return false;
        var getEntity = FastAccess.Func1<long, object?>(mgr.GetMethod("GetEntity", AnyInstance, null, new[] { typeof(long) }, null));
        var destroying = FastAccess.Getter<bool>(StellarInterop.FindPropertyUp(ent, "IsDestroying"));
        var top = FastAccess.Getter<long>(LongProperty(ent, "TopSummonUuid"));
        var summoner = FastAccess.Getter<long>(LongProperty(ent, "SummonUuid"));
        if (getEntity is null || destroying is null || top is null || summoner is null) return false;
        (_getEntity, _destroying, _topSummonUuid) = (getEntity, destroying, top);
        _summonUuid = summoner;   // set last: the "fully resolved" sentinel
        return true;
    }

    /// <summary>Only an <c>Int64</c> getter qualifies — a differently-typed member of the same name after a game patch
    /// leaves the lookup unresolved (fail open) rather than reading it with the wrong width.</summary>
    private static PropertyInfo? LongProperty(Type t, string name) =>
        StellarInterop.FindPropertyUp(t, name) is { PropertyType: var pt } p && pt == typeof(long) ? p : null;

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnLookedUp(long uuid, SummonerRead read, long owner);
}
