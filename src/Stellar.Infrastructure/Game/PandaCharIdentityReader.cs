using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reads the local player's stable identity — display name, role level, current
/// profession, char id — from the live <c>Zproto.CharSerialize</c> record instead
/// of the world entity's attribute bag.
///
/// <para><b>Why a second source exists.</b> <see cref="PandaPlayerStateProbe"/>
/// reads everything off <c>ZEntityMgr</c>'s player entity via
/// <c>TryGetAttr&lt;T&gt;(EAttrType)</c>. That attribute bag can be empty while
/// the client plainly knows who the player is — reproduced by relaunching while
/// mounted, where the probe logs <c>hp=0, stamina=0, lvl=0, name=''</c> and every
/// <c>IPlayerState</c> consumer degrades together (the CombatMeter's own row
/// falls back to the literal <c>"Self"</c> with no class crest). Swapping which
/// accessor we read on the manager cannot help: <c>MainEntity</c>, <c>MainEnt</c>,
/// <c>PlayerEntity</c> and <c>PlayerEnt</c> all return the single
/// <c>playerEnt_</c> field. See
/// <c>docs/recon/playerstate-probe-mounted-blackout.md</c>.</para>
///
/// <para><b>The chain walked here</b> (names confirmed against the
/// <c>recon/cpp2il-out</c> metadata):
/// <list type="bullet">
///   <item><c>CharSerialize.CharBase</c> → <c>CharBaseInfo.Name</c></item>
///   <item><c>CharSerialize.RoleLevel</c> → <c>RoleLevel.Level</c></item>
///   <item><c>CharSerialize.ProfessionList</c> → <c>ProfessionList.CurProfessionId</c></item>
///   <item><c>CharSerialize.CharId</c></item>
/// </list>
/// <c>CurProfessionId</c> — NOT <c>CharBaseInfo.InitProfessionId</c>, which is the
/// character's INITIAL profession and would ship the wrong crest for anyone who
/// has ever switched class.</para>
///
/// <para>Members are resolved as property-or-field: the cpp2il stub renders the
/// proto members as fields while the live assembly exposes properties (the
/// working inventory reader resolves them with <c>GetProperty</c>), so member
/// kind from the dump is not trustworthy and both are tried. Leaf members are
/// resolved off the live child instance's runtime type for the same reason.</para>
/// </summary>
internal sealed class PandaCharIdentityReader
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Re-walk cadence once an identity is known. The identity is stable for a
    // session apart from a profession switch, and this runs on the per-tick
    // service refresh, so a full reflection walk every tick is pure waste. While
    // nothing is known yet we walk every call so boot picks it up as soon as the
    // record lands.
    private const int RecheckInterval = 64;

    private readonly IPluginLog _log;
    private readonly Func<object?> _readCharSerialize;

    // Top-level members off CharSerialize.
    private Type? _resolvedForType;
    private ReflectedMember _charId;
    private ReflectedMember _charBase;
    private ReflectedMember _roleLevel;
    private ReflectedMember _professionList;

    // Leaf members, resolved lazily off each child's runtime type.
    private ReflectedMember _nameLeaf;
    private ReflectedMember _levelLeaf;
    private ReflectedMember _professionLeaf;

    private CharIdentity _cached;
    private bool _hasCached;
    private int _callsSinceWalk;
    private bool _firstReadLogged;

    public PandaCharIdentityReader(IPluginLog log, Func<object?> readCharSerialize)
    {
        _log = log;
        _readCharSerialize = readCharSerialize;
    }

    /// <summary>
    /// Attempts to read the identity. Returns <c>false</c> when the record is not
    /// readable yet AND nothing has ever been read — never returns <c>true</c>
    /// with an all-empty identity, so the caller can treat <c>false</c> as "not
    /// known yet" rather than "cleared".
    /// </summary>
    internal bool TryRead(out CharIdentity identity)
    {
        if (_hasCached && ++_callsSinceWalk < RecheckInterval)
        {
            identity = _cached;
            return true;
        }
        _callsSinceWalk = 0;

        var record = ReadRecord();
        if (record is null)
        {
            return ServeCached(out identity);
        }

        EnsureTopLevelResolved(record.GetType());

        var name = ReadVia(record, _charBase, ref _nameLeaf, "Name") as string;
        var level = ToInt(ReadVia(record, _roleLevel, ref _levelLeaf, "Level"));
        var profession = ToInt(ReadVia(record, _professionList, ref _professionLeaf, "CurProfessionId"));
        var charId = ToLong(_charId.Get(record));

        // Nothing usable — keep whatever we had rather than publishing blanks.
        if (string.IsNullOrEmpty(name) && level <= 0 && profession <= 0)
        {
            return ServeCached(out identity);
        }

        _cached = new CharIdentity(charId, name, level, profession);
        _hasCached = true;
        identity = _cached;
        LogFirstRead();
        return true;
    }

    private object? ReadRecord()
    {
        try { return _readCharSerialize(); }
        catch { return null; }
    }

    private bool ServeCached(out CharIdentity identity)
    {
        identity = _cached;
        return _hasCached;
    }

    private void LogFirstRead()
    {
        if (_firstReadLogged) return;
        _firstReadLogged = true;
        _log.Info($"[PlayerState] char-record identity: name='{_cached.Name}' lvl={_cached.Level} " +
                  $"prof={_cached.Profession} charId={_cached.CharId} (source=CharSerialize, survives entity blackout)");
    }

    // Resolves the four top-level members once per record type. Idempotent.
    private void EnsureTopLevelResolved(Type recordType)
    {
        if (_resolvedForType == recordType) return;
        _resolvedForType = recordType;
        _charId = ReflectedMember.Resolve(recordType, "CharId");
        _charBase = ReflectedMember.Resolve(recordType, "CharBase");
        _roleLevel = ReflectedMember.Resolve(recordType, "RoleLevel");
        _professionList = ReflectedMember.Resolve(recordType, "ProfessionList");
    }

    // Reads record.<parent>.<leafName>, resolving the leaf off the child's live
    // runtime type on first use. Null when either hop is absent.
    private static object? ReadVia(object record, ReflectedMember parent, ref ReflectedMember leaf, string leafName)
    {
        var child = parent.Get(record);
        if (child is null) return null;
        if (!leaf.Ok)
        {
            leaf = ReflectedMember.Resolve(child.GetType(), leafName);
        }
        return leaf.Get(child);
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => unchecked((int)l),
        uint u => unchecked((int)u),
        ulong ul => unchecked((int)ul),
        short s => s,
        _ => 0,
    };

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        uint u => u,
        ulong ul => unchecked((long)ul),
        _ => 0L,
    };

    /// <summary>
    /// A resolved instance member that may be either a property or a field —
    /// see the type remarks for why both are tried. Reads swallow reflection /
    /// marshal failures and yield null, matching the surrounding probes.
    /// </summary>
    private readonly struct ReflectedMember
    {
        private readonly PropertyInfo? _property;
        private readonly FieldInfo? _field;

        private ReflectedMember(PropertyInfo? property, FieldInfo? field)
        {
            _property = property;
            _field = field;
        }

        internal bool Ok => _property is not null || _field is not null;

        internal static ReflectedMember Resolve(Type owner, string name)
        {
            try
            {
                var property = owner.GetProperty(name, AnyInstance);
                if (property is not null) return new ReflectedMember(property, null);
                return new ReflectedMember(null, owner.GetField(name, AnyInstance));
            }
            catch
            {
                return default;
            }
        }

        internal object? Get(object? target)
        {
            if (target is null) return null;
            try
            {
                if (_property is not null) return _property.GetValue(target);
                return _field?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }
    }
}

/// <summary>
/// The local player's slow-moving identity as read from the char record.
/// Infrastructure-internal; mapped onto <c>PlayerIdentitySnapshot</c> at the
/// Application boundary.
/// </summary>
internal readonly record struct CharIdentity(long CharId, string? Name, int Level, int Profession);
