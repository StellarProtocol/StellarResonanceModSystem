using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>A model's look-at switches before posing (the <c>AnimLookAtComp</c> reads <see cref="LookAtBackend"/> uses),
/// so resetting the local player's head and eyes returns them to exactly that. Null when unreadable. Main thread.</summary>
internal sealed class LookAtSnapshotReader
{
    private readonly IGameTypeRegistry _types;
    private PropertyInfo? _comp;
    private MethodInfo? _look, _head, _eye;

    public LookAtSnapshotReader(IGameTypeRegistry types) => _types = types;

    public LookAtSnapshot? Read(object model)
    {
        if (!Resolve()) return null;
        try
        {
            var comp = _comp!.GetValue(model);
            if (comp is null) return null;
            return new LookAtSnapshot(_look!.Invoke(comp, null) as bool?, _head!.Invoke(comp, null) as bool?, _eye!.Invoke(comp, null) as bool?);
        }
        catch { return null; }
    }

    private bool Resolve()
    {
        if (_eye is not null) return true;
        var model = _types.FindType(GameEntityAccess.ModelType);
        var comp = _types.FindType(LookAtBackend.LookCompType);
        if (model is null || comp is null) return false;
        _comp = StellarInterop.FindPropertyUp(model, "AnimLookAtComp");
        _look = StellarInterop.FindMethod(comp, "lookEnable", 0);
        _head = StellarInterop.FindMethod(comp, "headLookEnable", 0);
        var eye = StellarInterop.FindMethod(comp, "eyeLookEnable", 0);
        if (_comp is null || _look is null || _head is null || eye is null) return false;
        _eye = eye;
        return true;
    }
}
