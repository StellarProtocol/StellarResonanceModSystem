using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Resonance (Battle Imagine) read concern of <see cref="PandaInventoryPullReader"/>.
/// Walks <c>CharSerialize.Resonance.Installed</c> (proto field 28 → repeated
/// uint32) into the equipped Imagine id list, in slot order
/// ([0]=left/X, [1]=right/Z). Mirrors the inventory walk: the same reflection
/// handles (resolved in the Bootstrap partial) and the same
/// <c>CollectInts</c> RepeatedField walker the module-parts read uses.
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    // Reads CharSerialize.Resonance.Installed → List<int>. Returns an empty
    // list (never null) when the Resonance member is absent/empty. Property
    // handles are normally resolved in ResolveCharSerializeProperties; if a
    // capture-only path resolved CharSerialize without them, fall back to a
    // lazy resolve off the live instance type (mirrors EnsureModSlotHandles).
    private IReadOnlyList<int> ReadInstalledResonances(object charSerialize)
    {
        EnsureResonanceHandles(charSerialize.GetType());
        if (_resonanceProperty is null || _resonanceInstalledProperty is null)
        {
            return EmptyInstalled;
        }

        object? resonance;
        try { resonance = _resonanceProperty.GetValue(charSerialize); }
        catch { return EmptyInstalled; }
        if (resonance is null) return EmptyInstalled;

        object? installedRaw;
        try { installedRaw = _resonanceInstalledProperty.GetValue(resonance); }
        catch { return EmptyInstalled; }
        if (installedRaw is null) return EmptyInstalled;

        var ids = CollectInts(installedRaw);
        if (ids.Count == 0) return EmptyInstalled;

        // CollectInts returns int[] or List<int>, both already IReadOnlyList<int>.
        return ids as IReadOnlyList<int> ?? new List<int>(ids);
    }

    // Lazily resolve the Resonance + Installed handles off the live CharSerialize
    // type if the full resolver hadn't (e.g. a capture-only resolution path).
    // Idempotent and additive — never clobbers handles already set.
    private void EnsureResonanceHandles(Type charSerializeType)
    {
        _resonanceProperty ??= charSerializeType.GetProperty("Resonance", AnyInstance);
        if (_resonanceProperty is null) return;
        _resonanceInstalledProperty ??= FindMapLikeProperty(_resonanceProperty.PropertyType, "Installed");
    }
}
