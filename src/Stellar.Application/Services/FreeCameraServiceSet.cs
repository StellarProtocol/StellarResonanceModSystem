using Stellar.Abstractions.Services;
namespace Stellar.Application.Services;

/// <summary>The free-camera services handed to the aggregator as one argument (keeps Host's
/// <c>ConstructPluginServices</c> under the 50-LoC analyzer gate).</summary>
internal sealed record FreeCameraServiceSet(
    ICameraOverride Camera, IInputShield Shield, ISceneFreeze Freeze, IEmotes Emotes, ICombatState Combat, IEntityPicker Picker);
