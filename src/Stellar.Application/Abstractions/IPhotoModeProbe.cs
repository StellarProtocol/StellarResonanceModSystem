using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

internal interface IPhotoModeProbe
{
    /// <summary>Raised on the main thread with the new kind (None = left photo mode).</summary>
    event Action<PhotoModeKind>? KindChanged;
    /// <summary>Raised on the main thread when a cutscene starts (true) or ends (false).</summary>
    event Action<bool>? CutsceneChanged;
}
