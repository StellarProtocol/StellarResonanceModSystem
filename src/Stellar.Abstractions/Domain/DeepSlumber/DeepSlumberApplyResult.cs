namespace Stellar.Abstractions.Domain.DeepSlumber;

/// <summary>The outcome of <see cref="Stellar.Abstractions.Services.IDeepSlumber.ApplySetupAsync"/>.</summary>
public enum DeepSlumberApplyResult
{
    /// <summary>Every required operation succeeded.</summary>
    Success,
    /// <summary>The live state already equalled the target — no operations were issued.</summary>
    AlreadyMatched,
    /// <summary>Some operations succeeded and some failed (e.g. a missing factor item). The
    /// line change may still have landed; the game toasts each failure itself.</summary>
    PartialFailure,
    /// <summary>Nothing was applied — the game refused the first (line-enable) operation (combat
    /// lock, cost, unlock, …); the game toasts the reason.</summary>
    Refused,
    /// <summary>The write API or the live state was not resolved yet; nothing was attempted.</summary>
    Unavailable,
    /// <summary>The apply was cancelled before any operation completed; nothing was applied.</summary>
    Cancelled,
}
