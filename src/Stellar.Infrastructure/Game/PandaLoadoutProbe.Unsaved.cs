using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The cached "worn setup has unsaved changes" flag for <see cref="PandaLoadoutProbe"/>
/// (<c>ILoadoutSave.HasUnsavedChanges</c>). The value is the game's OWN check,
/// <c>weapon_vm.lua:362</c> <c>WeaponVM.CheckRolePlanIsChange()</c> — synchronous, dot-call, returns a
/// bool; it compares the live class/gear/modules/talents with the worn plan's saved data and is the check
/// behind the game's switch warning <c>RolePlanNotSaveTipForSwitch</c> and its "nothing to save" tip 150210.
///
/// <para><b>Event-driven, never polled (owner doctrine 2026-08-23), and free.</b> The check rides the two
/// chunks that ALREADY run when one of its inputs can have changed — it is an extra <c>UNSAVED</c> row
/// (<see cref="UnsavedRowFragment"/>) at the end of:</para>
/// <list type="bullet">
///   <item>the merge-event <c>LiveStateChunk</c> (the LIVE containers moved), and</item>
///   <item>the on-demand <c>RefreshChunk</c>, AFTER its <c>SyncProjectList</c> (the SAVED plan data moved —
///   including the refresh a successful save arms).</item>
/// </list>
/// <para>So it costs no extra <c>DoString</c> and no extra global read. Chosen over the debounced
/// per-class resolve because that step runs only after the item container is ready and is gated on
/// <c>_resolvePending</c>; the two chunks above run exactly on the flag's two inputs.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    /// <summary>Lua fragment appended to a chunk that has already built its <c>out</c> string: adds
    /// <c>"\nUNSAVED\t1|0|E:&lt;err&gt;"</c>. Scoped in <c>do … end</c> (no locals leak into the host
    /// chunk) and self-pcall'd, so a failing check can never break the host chunk's other rows; the error
    /// text is flattened (no newline/tab) so it cannot forge rows. No interpolation — no injection
    /// surface.</summary>
    internal const string UnsavedRowFragment =
        " do local uc=\"E\"" +
        " local uok,uerr=pcall(function() local c=Z.VMMgr.GetVM(\"weapon\").CheckRolePlanIsChange() if c==true then uc=\"1\" elseif c==false then uc=\"0\" end end)" +
        " if not uok then uc=\"E:\"..(string.gsub(tostring(uerr),\"[\\r\\n\\t]\",\" \")) end" +
        " out=out..\"\\nUNSAVED\\t\"..uc end";

    private const string UnsavedRowPrefix = "UNSAVED\t";

    private volatile bool _hasUnsavedChanges;

    /// <summary>See <c>ILoadoutSave.HasUnsavedChanges</c>.</summary>
    public bool HasUnsavedChanges => _hasUnsavedChanges;

    /// <summary>Pure parse of the check's value: "1" → true, "0" → false, anything else (the "E…" error
    /// marker, unset) → null = no signal, keep the cached value.</summary>
    internal static bool? ParseUnsavedFlag(string? raw) => raw switch
    {
        "1" => true,
        "0" => false,
        _   => null,
    };

    /// <summary>Pure: the <c>UNSAVED</c> row's value from a chunk dump, or null when the dump carries no
    /// such row (an old/failed read — no signal).</summary>
    internal static string? FindUnsavedRow(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith(UnsavedRowPrefix, StringComparison.Ordinal)) return line.Substring(UnsavedRowPrefix.Length);
        }
        return null;
    }

    // Main thread: apply a dump's UNSAVED row to the cached flag. No row / an error → keep the cache.
    private void ApplyUnsavedRow(string raw)
    {
        var row = FindUnsavedRow(raw);
        if (ParseUnsavedFlag(row) is not { } value)
        {
            DiagUnsavedCheckFailed(row);
            return;
        }
        if (value == _hasUnsavedChanges) return;
        _hasUnsavedChanges = value;
        DiagUnsavedChanged(value);
    }
}
