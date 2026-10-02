using System;
namespace Stellar.Infrastructure.Game;

/// <summary>One sampled monster / boss across a freeze (combat-freeze evidence capture, diagnostics only): the pointers
/// first seen (to report a model / component / controller swap), and per-entity sample tallies for the unfreeze summary.
/// Pointers are identity keys only — never dereferenced.</summary>
internal sealed class FreezeDiagRow
{
    public FreezeDiagRow(long uuid, int kind)
    {
        Uuid = uuid;
        Kind = kind;
    }

    public long Uuid { get; }
    public int Kind { get; }
    public IntPtr FirstModel { get; set; }
    public IntPtr FirstComp { get; set; }
    public IntPtr FirstController { get; set; }
    public IntPtr FirstGo { get; set; }
    public string FirstColls { get; set; } = "";
    public int Samples { get; set; }
    public int DrawnAnimating { get; set; }
    public int ControllerAnimating { get; set; }
    public int OffHold { get; set; }
    public float MaxOffHold { get; set; }
    public bool Untargeted { get; set; }
    public bool NeverFrozen { get; set; }
    public bool NotHeld { get; set; }
    public bool Untracked { get; set; }
    public bool Swapped { get; set; }
    public int LastLogged { get; set; } = -1;

    /// <summary>Records this sample's pointers; true when any differs from the first one seen (a swap / recycle).</summary>
    public bool NotePointers(IntPtr model, IntPtr comp, IntPtr controller, IntPtr go)
    {
        if (Samples == 0)
        {
            FirstModel = model;
            FirstComp = comp;
            FirstController = controller;
            FirstGo = go;
            return false;
        }
        var swap = Differs(FirstModel, model) || Differs(FirstComp, comp) || Differs(FirstController, controller) || Differs(FirstGo, go);
        Swapped |= swap;
        return swap;
    }

    /// <summary>"model,comp,ctl,go" — which pointers changed since the first sample ("-" for none).</summary>
    public string SwapText(IntPtr model, IntPtr comp, IntPtr controller, IntPtr go)
    {
        var s = (Differs(FirstModel, model) ? "model," : "") + (Differs(FirstComp, comp) ? "comp," : "") +
                (Differs(FirstController, controller) ? "ctl," : "") + (Differs(FirstGo, go) ? "go," : "");
        return s.Length == 0 ? "-" : s.TrimEnd(',');
    }

    private static bool Differs(IntPtr first, IntPtr now) => first != IntPtr.Zero && now != IntPtr.Zero && first != now;
}
