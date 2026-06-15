using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Generic registry of <see cref="ProfileCardActionSpec"/>s. Implements both the plugin-facing write side
/// (<see cref="IProfileCardActions"/> — <see cref="Register"/> adds and returns a disposable that removes)
/// and the Infrastructure-facing read side (<see cref="IProfileCardActionSource"/> — <see cref="Actions"/>).
/// Replaces the single-purpose <c>InspectRequestBus</c>: any plugin can now contribute a native profile-card
/// button, and the EntityInspector registers its "Inspect" action through this.
///
/// <para>Registration is main-thread (plugin construction + the per-tick card injector both run on the Panda
/// Update loop), but the list is locked anyway so a registration/disposal can't tear a concurrent read.
/// Registrations are deduped by <see cref="ProfileCardActionSpec.Id"/>: a re-register with a live id replaces
/// the prior spec in place (keeps registration order stable).</para>
/// </summary>
public sealed class ProfileCardActionRegistry : IProfileCardActions, IProfileCardActionSource
{
    private readonly object _gate = new();
    private readonly List<ProfileCardActionSpec> _actions = new();

    /// <inheritdoc/>
    public IReadOnlyList<ProfileCardActionSpec> Actions
    {
        get { lock (_gate) return _actions.ToArray(); }
    }

    /// <inheritdoc/>
    public IDisposable Register(ProfileCardActionSpec spec)
    {
        if (spec is null) throw new ArgumentNullException(nameof(spec));
        lock (_gate)
        {
            var existing = _actions.FindIndex(a => a.Id == spec.Id);
            if (existing >= 0) _actions[existing] = spec;   // dedupe-by-Id: replace in place
            else _actions.Add(spec);
        }
        return new Registration(this, spec);
    }

    private void Remove(ProfileCardActionSpec spec)
    {
        lock (_gate)
        {
            // Reference-identity remove so a dispose only pulls the exact spec it registered (a later
            // re-register under the same id swaps the slot, and the stale handle must then be a no-op).
            for (var i = 0; i < _actions.Count; i++)
            {
                if (ReferenceEquals(_actions[i], spec)) { _actions.RemoveAt(i); return; }
            }
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly ProfileCardActionRegistry _owner;
        private readonly ProfileCardActionSpec _spec;
        private bool _disposed;

        public Registration(ProfileCardActionRegistry owner, ProfileCardActionSpec spec)
        {
            _owner = owner;
            _spec = spec;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Remove(_spec);
        }
    }
}
