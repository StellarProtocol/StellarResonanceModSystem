using System;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>The local player's logical position for a per-frame caller (the free camera's distance cap, review F2):
/// compiled accessors for <c>PlayerUuid</c>, <c>GetEntity</c>, <c>IsDestroying</c>, <c>Model</c> and
/// <c>GetAttrGoPosition</c> — the same lookups and liveness gates as <see cref="LiveModel"/>(<see cref="LocalEntity"/>)
/// + <see cref="AttrPosition"/>, but no <c>MethodInfo.Invoke</c>, no boxing and one manager fetch per call. Falls back to
/// the reflection path when compilation is unavailable on this client.</summary>
internal sealed partial class GameEntityAccess
{
    private Func<object, long>? _fastPlayerUuid;
    private Func<object, long, object?>? _fastGetEntity;
    private Func<object, bool>? _fastEntGone, _fastModelGone;
    private Func<object, object?>? _fastModel;
    private Func<object, Vector3>? _fastAttrPos;
    private bool _fastTried;

    /// <summary>The local player's <c>GetAttrGoPosition</c>, or null outside the world / while it despawns.</summary>
    public Vector3? LocalPlayerPosition()
    {
        if (Manager() is not { } mgr) return null;   // resolves the handles; the one manager fetch of this call
        if (!ResolveFast()) return SlowLocalPlayerPosition();
        try
        {
            var uuid = _fastPlayerUuid!(mgr);
            if (uuid == 0 || _fastGetEntity!(mgr, uuid) is not { } e || _fastEntGone!(e)) return null;
            if (_fastModel!(e) is not { } model || _fastModelGone!(model)) return null;
            return _fastAttrPos!(model);
        }
        catch { return null; }
    }

    private Vector3? SlowLocalPlayerPosition() => LiveModel(LocalEntity()) is { } m ? AttrPosition(m) : null;

    // Called only after Resolve() succeeded (Manager() returned non-null), so every member handle is set.
    private bool ResolveFast()
    {
        if (_fastTried) return _fastAttrPos is not null;
        _fastTried = true;
        _fastPlayerUuid = FastAccess.Getter<long>(_playerUuid);
        _fastGetEntity = FastAccess.Func1<long, object?>(_getEntity);
        _fastEntGone = FastAccess.Getter<bool>(_entDestroying);
        _fastModel = FastAccess.Getter<object?>(_model);
        _fastModelGone = FastAccess.Getter<bool>(_modelDestroying);
        var attrPos = FastAccess.StaticFunc1<object, Vector3>(_attrPos);
        if (_fastPlayerUuid is null || _fastGetEntity is null || _fastEntGone is null || _fastModel is null ||
            _fastModelGone is null || attrPos is null) return false;
        _fastAttrPos = attrPos;   // set last: the "fast path available" sentinel
        return true;
    }
}
