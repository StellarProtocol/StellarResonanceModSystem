using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>The local player's observable look-at switches (null = could not be read).</summary>
internal readonly record struct LookAtSnapshot(bool? Enable, bool? Head, bool? Eye);

internal enum LookAtWriteKind { HeadClose, EyeOpen, Enable }

/// <summary>One corrective write: HeadClose(Value) / EyeOpen(Value) / Enable(Value) on the model.</summary>
internal readonly record struct LookAtWrite(LookAtWriteKind Kind, bool Value);

/// <summary>What to write back after the game's own look-at release recipe, so the model returns to the snapshot
/// (recon run 2 F: the recipe leaves head=False where it was True; <c>HeadClose(!pre.Head)</c> fixes it).</summary>
internal static class LookAtRestorePlan
{
    internal static List<LookAtWrite> Corrections(LookAtSnapshot pre, LookAtSnapshot? after)
    {
        var writes = new List<LookAtWrite>(3);
        if (pre.Head is bool head && after?.Head != head) writes.Add(new LookAtWrite(LookAtWriteKind.HeadClose, !head));
        if (pre.Eye is bool eye && after?.Eye != eye) writes.Add(new LookAtWrite(LookAtWriteKind.EyeOpen, eye));
        if (pre.Enable is bool enable && after?.Enable != enable) writes.Add(new LookAtWrite(LookAtWriteKind.Enable, enable));
        return writes;
    }
}
