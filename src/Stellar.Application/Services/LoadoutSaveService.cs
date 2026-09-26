using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Exposes <see cref="ILoadoutSave"/> over the Infrastructure <see cref="ILoadoutSaveProbe"/>.
/// A pass-through: every decision (pre-dispatch refusals, the game dispatch, result parsing, the
/// post-save list refresh and the event-driven unsaved-changes read) lives in the probe, next to the
/// switch path it has to coordinate with.</summary>
internal sealed class LoadoutSaveService : ILoadoutSave
{
    private readonly ILoadoutSaveProbe _probe;

    public LoadoutSaveService(ILoadoutSaveProbe probe) => _probe = probe;

    public bool HasUnsavedChanges => _probe.HasUnsavedChanges;

    public Task<LoadoutResult> SaveCurrentToAsync(int index, CancellationToken ct = default)
        => _probe.CallSaveAsync(index, ct);
}
