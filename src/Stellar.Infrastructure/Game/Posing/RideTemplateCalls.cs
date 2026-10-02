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
/// share a name resolve by exact parameter types. Game exceptions propagate. Main thread.
/// </summary>
internal sealed class RideTemplateCalls
{
    private const BindingFlags S = BindingFlags.Public | BindingFlags.Static;

    private readonly IGameTypeRegistry _types;
    private PropertyInfo? _gender;
    private MethodInfo? _state, _actionId, _getTpl, _getFade, _setTpl;

    public RideTemplateCalls(IGameTypeRegistry types) => _types = types;

    /// <summary>Reads the source; when the copy would crash, sets its ride template to <c>""</c> (keeping its fade).
    /// Null when the game types are not resolvable.</summary>
    public CloneGuardReading? Normalise(object entity, object model)
    {
        if (!Resolve()) return null;
        var gender = Convert.ToInt32(_gender!.GetValue(model));
        var state = Convert.ToInt32(_state!.Invoke(null, new[] { entity }));
        var actionId = Convert.ToInt32(_actionId!.Invoke(null, new[] { model }));
        var templateNull = _getTpl!.Invoke(null, new[] { model }) is null;
        var needs = CloneGuard.NeedsRideTemplateNormalise(gender, state, actionId, templateNull);
        if (needs)
        {
            var fade = _getFade!.Invoke(null, new[] { model });
            _setTpl!.Invoke(null, new[] { model, string.Empty, fade });
        }
        return new CloneGuardReading(gender, state, actionId, templateNull, needs);
    }

    private bool Resolve()
    {
        if (_setTpl is not null) return true;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var entity = _types.FindType(GameEntityAccess.EntityType);
        var ext = _types.FindType(GameEntityAccess.AttrExtType);
        if (model is null || entity is null || ext is null) return false;
        _gender = StellarInterop.FindPropertyUp(model, "ModelGender");
        _state = ext.GetMethod("GetAttrState", S, null, new[] { entity }, null);
        _actionId = ext.GetMethod("GetAttrActionInfoActionId", S, null, new[] { model }, null);
        _getTpl = ext.GetMethod("GetAttrAnimRideTemplate", S, null, new[] { model }, null);
        _getFade = ext.GetMethod("GetAttrAnimRideTemplateFade", S, null, new[] { model }, null);
        var set = ext.GetMethod("SetAttrAnimRideTemplate", S, null, new[] { model, typeof(string), typeof(string) }, null);
        if (_gender is null || _state is null || _actionId is null || _getTpl is null || _getFade is null) return false;
        _setTpl = set;   // set last: the "fully resolved" sentinel
        return set is not null;
    }
}
