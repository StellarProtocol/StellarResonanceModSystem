using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>How a previously-hidden effect (identified by <c>uid</c>) should be released: shown back through the
/// manager's live listing, shown back through the still-held instance, treated as ended (dropped, never shown), or
/// left alone because the listing could not be read this tick.</summary>
internal enum EffectReleaseState
{
    /// <summary>The live listing could not be read this tick — keep the record and retry on the next apply; never
    /// shown, never dropped.</summary>
    Unknown,
    /// <summary>Still listed in ZEffectManager — show it back through the manager.</summary>
    Manager,
    /// <summary>Not listed, but the held instance reads back as the same effect — show it back through the instance.</summary>
    Instance,
    /// <summary>Not listed and no usable instance (none held, destroyed, or recycled into a different effect) — drop
    /// it without showing anything.</summary>
    Ended,
}

/// <summary>Pure decision rule behind <see cref="EffectReleaseState"/> — no reflection, no IL2CPP, unit-tested in
/// isolation from <see cref="GameEffectVisibility"/>.</summary>
internal static class EffectReleaseRule
{
    /// <summary><paramref name="listed"/> null means the manager's live listing could not be read this tick (always
    /// <see cref="EffectReleaseState.Unknown"/>, regardless of instance state — we must not guess).
    /// <paramref name="instanceUid"/> is the uid read back from the held instance right now; null means no instance
    /// is held, or the read failed/threw — both mean "can't use it". <paramref name="instanceDestroyed"/> is checked
    /// BEFORE trusting the readback: ZEffect is pooled (OnRent/OnRecycle), so a destroyed/recycled wrapper's uid is
    /// meaningless even when it happens to equal <paramref name="uid"/>.</summary>
    public static EffectReleaseState Resolve(HashSet<long>? listed, long uid, long? instanceUid, bool instanceDestroyed)
    {
        if (listed is null) return EffectReleaseState.Unknown;
        if (listed.Contains(uid)) return EffectReleaseState.Manager;
        if (instanceDestroyed || instanceUid is null) return EffectReleaseState.Ended;
        return instanceUid.Value == uid ? EffectReleaseState.Instance : EffectReleaseState.Ended;
    }
}
