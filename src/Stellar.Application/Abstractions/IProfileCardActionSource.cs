using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Read side of the profile-card action registry (Application → Infrastructure). The native-card
/// injector reads <see cref="Actions"/> on each card-open and injects one button per registered spec, in
/// registration order. Mirrors the read/write split of <c>ISocialDataSink</c>/<c>SocialDataCache</c>.</summary>
public interface IProfileCardActionSource
{
    /// <summary>Registered actions, in registration order. Read on the main thread at card-open.</summary>
    IReadOnlyList<ProfileCardActionSpec> Actions { get; }
}
