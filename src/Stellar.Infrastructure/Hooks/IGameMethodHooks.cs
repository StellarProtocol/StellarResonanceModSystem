using System;

namespace Stellar.Infrastructure.Hooks;

/// <summary>The two HarmonyX installs the posing backend needs (<see cref="HarmonyGameMethodHooker"/> in the game; a
/// recording fake in the pinned <c>clone_nre_male_null_ridetpl_make_*</c> tests, which prove the photo-copy safety net's
/// postfix is installed and feeds the net).</summary>
internal interface IGameMethodHooks
{
    /// <summary>A prefix on every instance overload of <paramref name="methodName"/> (never skips the original).</summary>
    void PrefixAllOverloads(Type type, string methodName, Action<object?, object?[]> callback);

    /// <summary>A postfix on every non-void instance overload that hands <paramref name="callback"/> the return value.</summary>
    void PostfixResultAllOverloads(Type type, string methodName, Action<object?> callback);
}
