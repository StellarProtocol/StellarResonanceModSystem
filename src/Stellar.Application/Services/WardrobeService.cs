using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Wraps <see cref="IWardrobeProbe"/> to expose <see cref="IWardrobe"/>. Enforces the
/// single-apply-in-flight rule and maps the bare game code to <see cref="WardrobeResult"/>.</summary>
internal sealed class WardrobeService : IWardrobe
{
    private readonly IWardrobeProbe _probe;
    private int _inFlight;   // 0 = idle, 1 = an apply is outstanding

    public WardrobeService(IWardrobeProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved && _probe.IsInWorld;

    public IReadOnlyDictionary<int, int>? GetWornOutfit() => _probe.ReadWorn();

    public async Task<WardrobeResult> ApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct = default)
    {
        if (!_probe.IsResolved) return WardrobeResult.GameApiUnavailable;
        if (!_probe.IsInWorld) return WardrobeResult.PlayerNotInWorld;

        // Only one server-side apply may be in flight; reject a second concurrent switch.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return WardrobeResult.Rejected;
        try
        {
            var code = await _probe.CallApplyAsync(outfit, ct).ConfigureAwait(false);
            return MapCode(code);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    // Probe convention: >= 0 is the game's FashionWear code (0 = ok, positive = a game
    // EErrorCode → Rejected; the game toasts the specific reason); < 0 is an infrastructure
    // outcome. A known in-combat code maps to InCombat once identified in-game.
    private static WardrobeResult MapCode(int code) => code switch
    {
        0 => WardrobeResult.Success,
        -1 => WardrobeResult.Timeout,
        -2 => WardrobeResult.Cancelled,
        -3 => WardrobeResult.GameApiUnavailable,
        _ => WardrobeResult.Rejected,
    };
}
