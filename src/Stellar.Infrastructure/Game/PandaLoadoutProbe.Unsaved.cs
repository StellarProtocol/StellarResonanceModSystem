namespace Stellar.Infrastructure.Game;

/// <summary>
/// The cached "worn setup has unsaved changes" flag for <see cref="PandaLoadoutProbe"/>
/// (<c>ILoadoutSave.HasUnsavedChanges</c>). The value is the game's OWN check,
/// <c>weapon_vm.lua:362</c> <c>WeaponVM.CheckRolePlanIsChange()</c> — synchronous, dot-call, returns a
/// bool; it compares the live class/gear/modules/talents with the worn plan's saved data and is the check
/// behind the game's switch warning <c>RolePlanNotSaveTipForSwitch</c> and its "nothing to save" tip 150210.
///
/// <para><b>Event-driven, never polled (owner doctrine 2026-08-23).</b> Re-evaluated only when one of its
/// two inputs can have changed: the LIVE containers (the container-merge event, <c>RefreshLiveStateIfArmed</c>)
/// or the saved plan data (a new <c>SyncProjectList</c> dump in <c>ParseLoadoutData</c>, and a
/// successful save). Cost: one <c>DoString</c> of a local, non-yielding chunk.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private const string UnsavedGlobal = "_StellarLoadoutUnsaved";

    // pcall-guarded: a failed check writes "E" and the cached flag is KEPT (no-signal, never flipped on a
    // broken read). No interpolation — no injection surface. Nothing yields, so no coroutine wrapper.
    private const string UnsavedCheckChunk =
        " local r=\"E\"" +
        " local ok,err=pcall(function() local c=Z.VMMgr.GetVM(\"weapon\").CheckRolePlanIsChange() if c==true then r=\"1\" elseif c==false then r=\"0\" end end)" +
        " if not ok then r=\"E:\"..tostring(err) end" +
        " rawset(_G,\"" + UnsavedGlobal + "\", r)";

    private volatile bool _hasUnsavedChanges;

    /// <summary>See <c>ILoadoutSave.HasUnsavedChanges</c>.</summary>
    public bool HasUnsavedChanges => _hasUnsavedChanges;

    /// <summary>Pure parse of the check's global: "1" → true, "0" → false, anything else (the "E" error
    /// marker, unset) → null = no signal, keep the cached value.</summary>
    internal static bool? ParseUnsavedFlag(string? raw) => raw switch
    {
        "1" => true,
        "0" => false,
        _   => null,
    };

    // Main thread only (touches the Lua VM).
    private void RefreshUnsavedFlag()
    {
        if (!InvokeChunk(UnsavedCheckChunk)) return;
        var raw = ReadLuaGlobalString(UnsavedGlobal);
        if (ParseUnsavedFlag(raw) is not { } value)
        {
            DiagUnsavedCheckFailed(raw);
            return;
        }
        if (value == _hasUnsavedChanges) return;
        _hasUnsavedChanges = value;
        DiagUnsavedChanged(value);
    }
}
