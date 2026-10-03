using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>The game's own photo / selfie mode state.</summary>
public interface IPhotoModeState
{
    /// <summary>True while a game photo mode is active.</summary>
    bool IsActive { get; }
    /// <summary>Which mode is active.</summary>
    PhotoModeKind Kind { get; }
    /// <summary>Raised on entering a game photo mode.</summary>
    event Action<PhotoModeKind>? Entered;
    /// <summary>Raised on leaving it.</summary>
    event Action? Exited;
    /// <summary>True while a game cutscene plays.</summary>
    bool InCutscene { get; }
    /// <summary>Raised when <see cref="InCutscene"/> changes.</summary>
    event Action<bool>? CutsceneChanged;
}
