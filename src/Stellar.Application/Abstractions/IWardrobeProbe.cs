using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the game's fashion (wardrobe) system. Implemented in
/// Infrastructure (InternalsVisibleTo grants access).</summary>
internal interface IWardrobeProbe
{
    /// <summary>True once the game-side fashion bridge is resolved.</summary>
    bool IsResolved { get; }

    /// <summary>True when the local player is in world (apply is only legal in world).</summary>
    bool IsInWorld { get; }

    /// <summary>The current worn outfit (region→fashionId), or null if not readable yet.</summary>
    IReadOnlyDictionary<int, int>? ReadWorn();

    /// <summary>Dispatch FashionWear with <paramref name="outfit"/> and report the outcome as a
    /// single int. Convention: <c>&gt;= 0</c> is the game's FashionWear code (<c>0</c> = ok,
    /// positive = a game EErrorCode); <c>&lt; 0</c> is an infrastructure outcome:
    /// <c>-1</c> = timeout, <c>-2</c> = cancelled, <c>-3</c> = bridge/dispatch failure.</summary>
    Task<int> CallApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct);
}
