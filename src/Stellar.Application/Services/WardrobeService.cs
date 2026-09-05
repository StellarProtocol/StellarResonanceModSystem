using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Wraps <see cref="IWardrobeProbe"/> to expose <see cref="IWardrobe"/>. Enforces the
/// single-apply-in-flight rule (shared by the outfit switch AND the weapon-skin switch) and maps the
/// bare game code to <see cref="WardrobeResult"/>.</summary>
internal sealed class WardrobeService : IWardrobe
{
    private readonly IWardrobeProbe _probe;
    private int _inFlight;   // 0 = idle, 1 = an apply (outfit or weapon skin) is outstanding

    public WardrobeService(IWardrobeProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved && _probe.IsInWorld;

    public IReadOnlyDictionary<int, int>? GetWornOutfit() => _probe.ReadWorn();

    public WardrobeWeaponSkin? GetWornWeaponSkin() => _probe.ReadWornWeaponSkin();

    public Task<WardrobeResult> ApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct = default)
        => RunGuardedAsync(() => _probe.CallApplyAsync(outfit, ct));

    public Task<WardrobeResult> ApplyWeaponSkinAsync(int professionId, int skinId, CancellationToken ct = default)
        => RunGuardedAsync(() => _probe.CallApplyWeaponSkinAsync(professionId, skinId, ct));

    // ONE in-flight slot for both switch kinds: the game runs one fashion RPC at a time, and a plugin that
    // re-applies an outfit plus its weapon skin awaits the first before sending the second.
    private async Task<WardrobeResult> RunGuardedAsync(Func<Task<int>> call)
    {
        if (!_probe.IsResolved) return WardrobeResult.GameApiUnavailable;
        if (!_probe.IsInWorld) return WardrobeResult.PlayerNotInWorld;

        // Only one server-side apply may be in flight; reject a second concurrent switch.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return WardrobeResult.Rejected;
        try
        {
            var code = await call().ConfigureAwait(false);
            return MapCode(code);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    // Probe convention: >= 0 is the game's RPC code (0 = ok, positive = a game EErrorCode → Rejected;
    // the game toasts the specific reason); < 0 is an infrastructure outcome. A known in-combat code
    // maps to InCombat once identified in-game.
    private static WardrobeResult MapCode(int code) => code switch
    {
        0 => WardrobeResult.Success,
        -1 => WardrobeResult.Timeout,
        -2 => WardrobeResult.Cancelled,
        -3 => WardrobeResult.GameApiUnavailable,
        _ => WardrobeResult.Rejected,
    };
}
