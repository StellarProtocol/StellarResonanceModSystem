using System;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>The game calls <see cref="PhotoCopyMaker"/> makes the copy with (<see cref="PoseActionCalls"/> in the game; a
/// fake in the pinned <c>clone_nre_male_null_ridetpl_make_*</c> tests). Exceptions propagate. Main thread.</summary>
internal interface IPhotoCopyCalls
{
    /// <summary><c>CloneModelForPhoto(entity)</c>; null when the game is not ready.</summary>
    object? Clone(object entity);

    /// <summary><c>RecyclePhotoModel(copy)</c>; false when the call is unavailable.</summary>
    bool Recycle(object copy);

    /// <summary><c>ZModelManager.modelDict_.Count</c>, -1 when unreadable. Diagnostics only; never throws.</summary>
    int ModelCount();

    /// <summary>Where the per-recycle diagnostics line goes (null = silent).</summary>
    void DiagnosticsTo(Action<string> log);
}

/// <summary>The source reads and the one write the ride-template guard needs (<see cref="RideTemplateCalls"/> in the game,
/// a fake in the pinned tests). Game exceptions propagate. Main thread.</summary>
internal interface IRideTemplateAccess
{
    /// <summary>Null when every member is resolved; otherwise the first one the game does not have (a member miss is
    /// cached, never retried).</summary>
    string? Missing();

    /// <summary><c>ZModel.ModelGender</c> (Unknown 0, EM 1, EF 2).</summary>
    int Gender(object model);

    /// <summary><c>EntityAttrExtensions.GetAttrState(ZEntity)</c>.</summary>
    int State(object entity);

    /// <summary><c>GetAttrActionInfoActionId(ZModel)</c>.</summary>
    int ActionId(object model);

    /// <summary>True when <c>GetAttrAnimRideTemplate(ZModel)</c> is null.</summary>
    bool TemplateIsNull(object model);

    /// <summary><c>GetAttrAnimRideTemplateFade(ZModel)</c>, passed back unchanged.</summary>
    object? Fade(object model);

    /// <summary>The game's own <c>SetAttrAnimRideTemplate(ZModel, string, string)</c> (local attribute, no packet).</summary>
    void SetTemplate(object model, string template, object? fade);
}
