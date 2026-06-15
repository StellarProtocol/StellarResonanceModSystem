using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Holds the small set of mutable fields that the two
/// <see cref="PandaInventoryProbe"/> concerns — the reflection pull-reader and
/// the WorldNtf stub-capture decoder — share across threads. Introduced as the
/// first step of the C-14 split so the bidirectional coupling lives behind one
/// owner with an explicit, preserved thread-visibility contract; both concerns
/// will later receive the SAME instance.
///
/// <para><b>Thread model (preserved verbatim from the in-place fields):</b>
/// the stub-capture writes run on the network receive thread; the pull-read /
/// 1Hz-refresh reads run on the game thread. The two reference-typed fields are
/// <c>volatile</c> and published immutable-after-write (copy-on-write for the
/// equipped snapshot), so the swaps are lock-free. <see cref="CaptureHookActive"/>
/// is a set-once-to-true latch and is intentionally NOT volatile — it matches
/// the original field exactly (a stale <c>false</c> read merely defers the
/// capture-only fast path by one poll, which is benign).</para>
/// </summary>
internal sealed class InventoryProbeState
{
    // The maintained equipped set (slot → module uuid). Reseeded from a full
    // method-21 sync and mutated copy-on-write by method-22 dirty deltas.
    // Written on the network receive thread, read on the game thread at 1Hz;
    // the volatile reference + immutable-after-publish dict make the swap
    // lock-free. Null until the first full sync seeds it.
    private volatile IReadOnlyDictionary<int, long>? _equippedSnapshot;

    // The live CharSerialize, latched by the OnCallStub postfix. Written on the
    // network receive thread, read on the game thread — reference assignment is
    // atomic, no lock needed.
    private volatile object? _capturedCharSerialize;

    // True once the capture postfix is installed on WorldNtfStub.OnCallStub.
    // Set-once-to-true; matches the original non-volatile field exactly.
    private bool _captureHookActive;

    /// <summary>
    /// The maintained equipped set (slot → uuid), or <c>null</c> before the
    /// first full sync seeds it. Volatile read — see the type-level thread model.
    /// </summary>
    public IReadOnlyDictionary<int, long>? EquippedSnapshot => _equippedSnapshot;

    /// <summary>
    /// Publishes a new equipped snapshot copy-on-write (whole-dictionary
    /// replace, never an in-place mutation of the previously published dict).
    /// The caller is responsible for building <paramref name="next"/> as an
    /// immutable-after-publish dictionary, exactly as the original
    /// <c>ApplyModSlotDelta</c> / <c>ReseedEquippedFromSync</c> code did.
    /// </summary>
    public void PublishEquippedSnapshot(IReadOnlyDictionary<int, long>? next)
        => _equippedSnapshot = next;

    /// <summary>
    /// The latched CharSerialize captured from the WorldNtf method-21 full sync,
    /// or <c>null</c> before any capture. Volatile read.
    /// </summary>
    public object? CapturedCharSerialize
    {
        get => _capturedCharSerialize;
        set => _capturedCharSerialize = value;
    }

    /// <summary>
    /// True once the capture postfix is installed. Set-once-to-true latch.
    /// </summary>
    public bool CaptureHookActive
    {
        get => _captureHookActive;
        set => _captureHookActive = value;
    }
}
