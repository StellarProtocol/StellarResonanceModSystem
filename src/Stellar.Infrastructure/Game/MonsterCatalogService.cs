using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Resolves the config/template id for a live monster entity via IL2CPP
/// reflection, keying <c>Bokura.MonsterTableBase</c> rows for boss
/// classification.
///
/// <para>
/// <b>Recon-stub state:</b> <see cref="TryGetMonsterConfigId"/> returns
/// <c>null</c> until the in-game boss-recon spike (Task 1) confirms which
/// field carries the config id. The <see cref="MonsterCatalogService.Diagnostics"/>
/// partial runs an always-on one-shot dump (<c>[BossRecon]</c>) for the first
/// 8 appearing monster entities to surface the candidate fields live.
/// </para>
///
/// <para>
/// See <c>recon/replay-boss-identification-notes.md</c> for the confirmed
/// field once the in-game pass is complete.
/// </para>
///
/// <para>Main-thread only; do not call from the network receive thread.</para>
/// </summary>
internal sealed partial class MonsterCatalogService : IMonsterCatalog
{
    private const BindingFlags AnyInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // -----------------------------------------------------------------------
    // ZEntityMgr reflection handles (reused from EntityTransformsService path).
    // Resolved lazily on first DiagMonster call; not needed for TryGetMonsterConfigId
    // until the recon confirms the source.
    // -----------------------------------------------------------------------

    private PropertyInfo? _mgrInstanceProperty;
    private MethodInfo?   _getEntityMethod;
    private PropertyInfo? _modelProperty;

    // Candidate config-id fields on ZEntity / ZModel — resolved lazily once
    // handles are available. Field names are best-effort guesses from the
    // dump until the in-game pass confirms one of them.
    // TODO(boss-recon): update these field names + TryGetMonsterConfigId
    //   after reading the [BossRecon] log from the Ancient Purifier run.
    //   Candidates (from Panda.ZRpcGen / ZEntity dump, unverified):
    //     - ZEntity.ConfigId   (field name unconfirmed — check [BossRecon] log)
    //     - ZEntity.MonsterId  (alternate name)
    //     - ZModel field with "config"/"template"/"monster_id" in name
    //   See recon/replay-boss-identification-notes.md once written.
    private FieldInfo?    _entityConfigIdField;
    private PropertyInfo? _entityConfigIdProp;
    private bool          _configIdResolved;

    // Reusable single-element arg buffer.
    private readonly object[] _arg1 = new object[1];

    /// <summary>
    /// Creates a new <see cref="MonsterCatalogService"/>.
    /// </summary>
    /// <param name="log">Logger for boot/diagnostic output.</param>
    /// <param name="typeRegistry">Game type registry for IL2CPP reflection.</param>
    public MonsterCatalogService(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
    }

    // -----------------------------------------------------------------------
    // IMonsterCatalog
    // -----------------------------------------------------------------------

    /// <inheritdoc/>
    public int? TryGetMonsterConfigId(long entityUuid)
    {
        // TODO(boss-recon): implement after the in-game [BossRecon] dump confirms
        // the config-id source. Until then return null so callers treat every
        // monster as an unidentified add. Do NOT remove this stub — Tasks 2 and 3
        // depend on the IMonsterCatalog interface being live at compile time.
        return null;
    }

    // -----------------------------------------------------------------------
    // Handle resolution (called from Diagnostics partial)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves all ZEntityMgr / ZEntity reflection handles needed for the
    /// diagnostic dump. Does nothing if already resolved or prerequisites
    /// are absent (Panda.* not yet loaded).
    /// </summary>
    internal void EnsureHandlesResolved()
    {
        if (_mgrInstanceProperty is not null
         && _getEntityMethod    is not null
         && _modelProperty      is not null)
        {
            return;
        }

        try
        {
            TryResolveHandles();
        }
        catch
        {
            // Panda.* may not be loaded yet; silently leave unresolved.
        }
    }

    private void TryResolveHandles()
    {
        var mgrType = _typeRegistry.FindType("Panda.ZGame.ZEntityMgr");
        if (mgrType is null) return;

        var entityType = _typeRegistry.FindType("Panda.ZGame.ZEntity");
        if (entityType is null) return;

        var instanceProp = FindSingletonInstanceProperty(mgrType);
        if (instanceProp is null) return;

        var getEntity = mgrType.GetMethod(
            "GetEntity",
            AnyInstance,
            binder: null,
            types: new[] { typeof(long) },
            modifiers: null);
        if (getEntity is null) return;

        var modelProp = entityType.GetProperty("Model", AnyInstance);
        if (modelProp is null) return;

        _mgrInstanceProperty = instanceProp;
        _getEntityMethod     = getEntity;
        _modelProperty       = modelProp;
    }

    private static PropertyInfo? FindSingletonInstanceProperty(Type tMgr)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? singletonOpen = null;
            try
            {
                singletonOpen = assembly.GetType("ZUtil.ZSingleton`1", throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (singletonOpen is null) continue;

            try
            {
                var closed = singletonOpen.MakeGenericType(tMgr);
                var prop   = closed.GetProperty("Instance", AnyStatic);
                if (prop is not null) return prop;
            }
            catch
            {
                // Try next assembly.
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the live <c>ZEntity</c> for the given uuid via
    /// <c>ZEntityMgr.GetEntity(long)</c>. Returns <c>null</c> on any failure.
    /// </summary>
    internal object? ResolveEntity(long uuid)
    {
        if (_mgrInstanceProperty is null || _getEntityMethod is null) return null;

        try
        {
            var mgr = _mgrInstanceProperty.GetValue(null);
            if (mgr is null) return null;
            _arg1[0] = uuid;
            return _getEntityMethod.Invoke(mgr, _arg1);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>ZEntity.Model</c> property via reflection. Returns
    /// <c>null</c> on any failure.
    /// </summary>
    internal object? ReadModel(object entity)
    {
        try
        {
            return _modelProperty?.GetValue(entity);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort attempt to read a config/template id off <paramref name="entity"/>
    /// (a live <c>ZEntity</c> resolved from <c>ZEntityMgr</c>). Tries a set of
    /// candidate field/property names from the dump; returns <c>null</c> if none
    /// resolves to a non-zero int. Caches handles on first successful resolve.
    ///
    /// <para>
    /// TODO(boss-recon): once the [BossRecon] log identifies the correct field,
    /// remove the multi-candidate walk and read the confirmed field directly.
    /// </para>
    /// </summary>
    internal long? TryReadConfigIdFromEntity(object entity)
    {
        if (!_configIdResolved)
        {
            ResolveConfigIdHandles(entity.GetType());
        }

        try
        {
            if (_entityConfigIdField is not null)
            {
                var v = _entityConfigIdField.GetValue(entity);
                if (v is int i && i != 0) return i;
                if (v is long l && l != 0) return l;
            }

            if (_entityConfigIdProp is not null)
            {
                var v = _entityConfigIdProp.GetValue(entity);
                if (v is int i && i != 0) return i;
                if (v is long l && l != 0) return l;
            }

            // Fallback: scan ALL fields/props on ZEntity looking for "config"/"monster"/"template"
            // in the name — surfaces candidates the dump didn't catalogue yet.
            return ScanEntityForConfigId(entity);
        }
        catch
        {
            return null;
        }
    }

    private void ResolveConfigIdHandles(Type entityType)
    {
        _configIdResolved = true;

        // Try candidate field names from the ZEntity/ZRpcGen dump.
        // Update this list after the [BossRecon] log confirms the real name.
        string[] candidateFields = { "ConfigId", "configId_", "MonsterId", "monsterId_", "TemplateId", "templateId_" };
        foreach (var name in candidateFields)
        {
            var f = entityType.GetField(name, AnyInstance);
            if (f is not null && (f.FieldType == typeof(int) || f.FieldType == typeof(long)))
            {
                _entityConfigIdField = f;
                return;
            }

            var p = entityType.GetProperty(name, AnyInstance);
            if (p is not null && (p.PropertyType == typeof(int) || p.PropertyType == typeof(long)))
            {
                _entityConfigIdProp = p;
                return;
            }
        }
    }

    private static long? ScanEntityForConfigId(object entity)
    {
        var t = entity.GetType();
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var f in t.GetFields(flags))
        {
            if (!ContainsConfigHint(f.Name)) continue;
            if (f.FieldType != typeof(int) && f.FieldType != typeof(long)) continue;
            try
            {
                var v = f.GetValue(entity);
                if (v is int i && i != 0) return i;
                if (v is long l && l != 0) return l;
            }
            catch { /* skip */ }
        }

        foreach (var p in t.GetProperties(flags))
        {
            if (!ContainsConfigHint(p.Name)) continue;
            if (p.PropertyType != typeof(int) && p.PropertyType != typeof(long)) continue;
            try
            {
                var v = p.GetValue(entity);
                if (v is int i && i != 0) return i;
                if (v is long l && l != 0) return l;
            }
            catch { /* skip */ }
        }

        return null;
    }

    private static bool ContainsConfigHint(string name)
    {
        return name.IndexOf("Config", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Template", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Monster", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
