using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

// WindowSpec.Prebuild, stage 1 (driven by WindowService.Prebuild.cs at the login / character-select screen): build the
// window's tree WITHOUT its first apply, then park it. The first-open cost of a plugin window splits in two (raid log
// 11-10): build:tree (chrome + element tree — dungeonhub.confirm.raid 219.7 ms, chattools.main 56.8 ms, the layout
// toolbar 71.9 ms) and build:firstApply (every value lambda, Conditional branch and list row — dungeonhub.hub 191 ms,
// combatmeter.main 117 ms). The tree needs no game state, so it is built where a freeze doesn't matter; the first apply
// pulls plugin values and waits for the world (stage 2, a background warm: .BackgroundWarm.cs).
// Parked in the SAME call it was built in: the root never renders a frame, so it never flashes, never takes a raycast, and
// every ticker hit-test skips it (they all require activeInHierarchy). A mount is an activation (fresh ZAct), but the
// real show from hidden re-activates anyway (WindowService ActivateOnShow), so z-order behaves as for a cold mount.
internal sealed partial class WindowRenderer : IWindowStructureMount
{
    public object? MountStructure(WindowRegistration reg)
    {
        var t = MountCore(reg, structureOnly: true);
        if (t?.Root != null) t.Root.SetActive(false);
        return t;
    }
}

internal sealed partial class WindowBuilder
{
    public sealed partial class WindowToken
    {
        // Built by MountStructure: no apply has run yet. Its first apply (the in-world fill or the first open) re-measures
        // the content min width that the mount measured on the empty tree; ApplyValues clears it.
        internal bool StructureOnly;
    }
}
