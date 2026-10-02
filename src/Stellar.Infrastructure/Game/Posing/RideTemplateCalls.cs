using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>What the clone guard read on the source (for the log) and whether it normalised the ride template.</summary>
internal readonly record struct CloneGuardReading(int Gender, int State, int ActionId, bool TemplateNull, bool Normalised);

/// <summary>
/// The reads and the one write <see cref="CloneGuard"/> needs, the same ones the game's clone callback makes (release_3.7,
/// ilspycmd): <c>ZModel.ModelGender</c>, <c>EntityAttrExtensions.GetAttrState(ZEntity)</c>,
/// <c>GetAttrActionInfoActionId(ZModel)</c>, <c>GetAttrAnimRideTemplate / GetAttrAnimRideTemplateFade(ZModel)</c>, and the
/// game's own setter <c>SetAttrAnimRideTemplate(ZModel, string, string)</c> (local attribute, no packet). Overloads that
/// share a name resolve by exact parameter types. A missing game TYPE is retried (cheap lookup); a missing MEMBER of a
/// found type is cached as missing for the session (review M-1: no re-resolve on every open). Game exceptions propagate.
/// Main thread.
/// </summary>
internal sealed class RideTemplateCalls : IRideTemplateAccess
{
    private const BindingFlags S = BindingFlags.Public | BindingFlags.Static;

    private readonly IGameTypeRegistry _types;
    private PropertyInfo? _gender;
    private MethodInfo? _state, _actionId, _getTpl, _getFade, _setTpl;
    private string? _missingMember;   // cached member miss
    private bool _resolved;

    public RideTemplateCalls(IGameTypeRegistry types) => _types = types;

    public string? Missing()
    {
        if (_resolved || _missingMember is not null) return _missingMember;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var entity = _types.FindType(GameEntityAccess.EntityType);
        var ext = _types.FindType(GameEntityAccess.AttrExtType);
        if (model is null) return GameEntityAccess.ModelType;
        if (entity is null) return GameEntityAccess.EntityType;
        if (ext is null) return GameEntityAccess.AttrExtType;
        _missingMember = ResolveMembers(model, entity, ext);
        _resolved = _missingMember is null;
        return _missingMember;
    }

    public int Gender(object model) => Convert.ToInt32(_gender!.GetValue(model));
    public int State(object entity) => Convert.ToInt32(_state!.Invoke(null, new[] { entity }));
    public int ActionId(object model) => Convert.ToInt32(_actionId!.Invoke(null, new[] { model }));
    public bool TemplateIsNull(object model) => _getTpl!.Invoke(null, new[] { model }) is null;
    public object? Fade(object model) => _getFade!.Invoke(null, new[] { model });
    public void SetTemplate(object model, string template, object? fade) =>
        _setTpl!.Invoke(null, new[] { model, template, fade });

    // The first member the game does not have, or null when all resolved.
    private string? ResolveMembers(Type model, Type entity, Type ext)
    {
        _gender = StellarInterop.FindPropertyUp(model, "ModelGender");
        _state = ext.GetMethod("GetAttrState", S, null, new[] { entity }, null);
        _actionId = ext.GetMethod("GetAttrActionInfoActionId", S, null, new[] { model }, null);
        _getTpl = ext.GetMethod("GetAttrAnimRideTemplate", S, null, new[] { model }, null);
        _getFade = ext.GetMethod("GetAttrAnimRideTemplateFade", S, null, new[] { model }, null);
        _setTpl = ext.GetMethod("SetAttrAnimRideTemplate", S, null, new[] { model, typeof(string), typeof(string) }, null);
        if (_gender is null) return "ZModel.ModelGender";
        if (_state is null) return "GetAttrState";
        if (_actionId is null) return "GetAttrActionInfoActionId";
        if (_getTpl is null) return "GetAttrAnimRideTemplate";
        if (_getFade is null) return "GetAttrAnimRideTemplateFade";
        return _setTpl is null ? "SetAttrAnimRideTemplate" : null;
    }
}
