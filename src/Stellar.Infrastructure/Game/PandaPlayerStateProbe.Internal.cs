using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Phase 6 internal surface — exposes the existing reflection machinery to
/// sibling Infrastructure probes (currently <see cref="PandaPlayerStatsProbe"/>)
/// without widening the public API. All members are <c>internal</c> and reuse
/// already-resolved fields/methods on the main partial — no new resolution
/// work happens here.
/// </summary>
internal sealed partial class PandaPlayerStateProbe
{
    /// <summary>
    /// True once the manager + entity types have been resolved. Mirrors the
    /// post-condition guard inside <see cref="EnsureBootstrap"/> so callers can
    /// short-circuit before paying the singleton lookup cost.
    /// </summary>
    internal bool IsBootstrapped => _bootstrapped && _zEntityMgrType is not null;

    /// <summary>
    /// Reads the live <c>ZUtil.ZSingleton&lt;ZEntityMgr&gt;.Instance</c>. Returns
    /// null if the singleton is not yet initialised — caller should retry next
    /// tick.
    /// </summary>
    internal object? GetSingletonInstance() => ReadSingletonInstance();

    /// <summary>
    /// Returns the live local-player entity from the manager (MainEntity /
    /// PlayerEntity / MainEnt / PlayerEnt — whichever resolved at bootstrap).
    /// Null when no character is loaded.
    /// </summary>
    internal object? GetMainEntity(object mgr) => ReadEntity(mgr);

    /// <summary>
    /// Resolves a <c>Zproto.EAttrType</c> enum value from its integer code via
    /// <see cref="Enum.ToObject"/>. Returns null when the enum type itself is
    /// missing (hot-update assemblies not loaded). Note that
    /// <see cref="Enum.ToObject"/> succeeds even for ints that aren't defined
    /// members of the enum — the caller's downstream <c>TryGetAttr</c> will
    /// return false for those, which is acceptable.
    /// </summary>
    internal object? BoxEnumValue(int attrId)
    {
        if (_attrTypeEnum is null)
        {
            return null;
        }
        try
        {
            return Enum.ToObject(_attrTypeEnum, attrId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Invokes <c>ZEntity.TryGetAttr&lt;long&gt;</c> on the given entity for the
    /// boxed EAttrType key. Returns 0 if the attr isn't resolvable. Note: the
    /// underlying read path widens to long internally — the int-truncated
    /// return matches the existing <see cref="ReadAsLong(object, object)"/>
    /// signature used by the PlayerState snapshot path.
    /// </summary>
    internal long ReadAttrLong(object entity, object attrKey)
        => ReadAsLong(entity, attrKey);

    /// <summary>
    /// Invokes <c>ZEntity.TryGetAttr&lt;int&gt;</c> on the given entity for the
    /// boxed EAttrType key. Returns 0 if the attr isn't resolvable. Widens the
    /// int result to long for the IPlayerStats unified return type.
    /// </summary>
    internal long ReadAttrInt(object entity, object attrKey)
        => ReadAsInt(entity, attrKey);

    /// <summary>
    /// Calls <c>ZEntity.TryGetAttr&lt;long&gt;</c> on the given entity and returns
    /// the FULL long value (not truncated to int — unlike <see cref="ReadAttrLong"/>).
    /// <paramref name="hit"/> is true when the call succeeded; false when the
    /// game's <c>TryGetAttr&lt;long&gt;</c> returned false because the attribute
    /// is stored under a different storage type. On a wrong-T call the game
    /// emits a single <c>[Error : Unity] arr type err</c> log line; callers
    /// should memoize the storage type after the first frame to avoid 60Hz
    /// log spam. This is the storage-type probe entry point for
    /// <see cref="PandaPlayerStatsProbe"/>.
    /// </summary>
    internal long ReadAttrInt64WithHit(object entity, object attrKey, out bool hit)
    {
        hit = false;
        if (_entityTryGetAttrLong is null)
        {
            return 0L;
        }
        try
        {
            _args2[0] = attrKey;
            _args2[1] = BoxedZeroLong;
            var ok = _entityTryGetAttrLong.Invoke(entity, _args2);
            if (ok is true && _args2[1] is long v)
            {
                hit = true;
                return v;
            }
        }
        catch
        {
            // Reflection / IL2CPP marshal failure — treat as miss.
        }
        return 0L;
    }

    /// <summary>
    /// Calls <c>ZEntity.TryGetAttr&lt;int&gt;</c> on the given entity and widens
    /// the int result to long for the unified <see cref="long"/> return type.
    /// <paramref name="hit"/> is true when the call succeeded; false otherwise.
    /// Companion to <see cref="ReadAttrInt64WithHit"/>; same memoization rules
    /// apply at the call site.
    /// </summary>
    internal long ReadAttrInt32WithHit(object entity, object attrKey, out bool hit)
    {
        hit = false;
        if (_entityTryGetAttrInt is null)
        {
            return 0L;
        }
        try
        {
            _args2[0] = attrKey;
            _args2[1] = BoxedZeroInt;
            var ok = _entityTryGetAttrInt.Invoke(entity, _args2);
            if (ok is true && _args2[1] is int v)
            {
                hit = true;
                return v;
            }
        }
        catch
        {
            // Reflection / IL2CPP marshal failure — treat as miss.
        }
        return 0L;
    }

    /// <summary>
    /// Calls <c>ZEntity.TryGetAttr&lt;float&gt;</c> for float-stored attributes (e.g. cd-reduction 11760,
    /// cd-acceleration 11960/11980, versatility%) that miss as both Int64 and Int32. <paramref name="hit"/> is
    /// true on success. Same wrong-T / memoization caveats as the int variants.
    /// </summary>
    internal float ReadAttrSingleWithHit(object entity, object attrKey, out bool hit)
    {
        hit = false;
        if (_entityTryGetAttrFloat is null)
        {
            return 0f;
        }
        try
        {
            _args2[0] = attrKey;
            _args2[1] = BoxedZeroFloat;
            var ok = _entityTryGetAttrFloat.Invoke(entity, _args2);
            if (ok is true && _args2[1] is float v)
            {
                hit = true;
                return v;
            }
        }
        catch
        {
            // Reflection / IL2CPP marshal failure — treat as miss.
        }
        return 0f;
    }
}
