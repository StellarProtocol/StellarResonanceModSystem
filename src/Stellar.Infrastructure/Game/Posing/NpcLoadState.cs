namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// What the NPC pose model holds across its async load (Task 4 review carry-overs): the callbacks' keep-alive, only while
/// the load is pending — released on load, on error and on a close while pending (every camera release, a zone change
/// included, closes the model; a load that never calls back leaks nothing); and the model reference, dropped the moment it
/// is handed out for removal so a recycled model is never read again. The order of load / error / close is
/// <see cref="NpcLoadGate"/>'s. Pure (unit-tested).
/// </summary>
/// <remarks>Releasing the keep-alive before a late callback is safe: an Il2CppInterop-converted delegate's target is an
/// injected <c>Il2CppToMonoDelegateReference</c> that holds the managed delegate for as long as the game holds the native
/// delegate, so a load arriving after a close still runs and removes its model.</remarks>
internal sealed class NpcLoadState
{
    private readonly NpcLoadGate _gate = new();
    private object[]? _keepAlive;
    private object? _model;
    private bool _ready, _settled;

    /// <summary>The load's callbacks are still referenced (the load is pending).</summary>
    public bool Holding => _keepAlive is not null;

    /// <summary>The generated model: being made (pre-create seen) or posable; null once handed out for removal or failed.</summary>
    public object? Model => _model;

    /// <summary>The request was made. Ignored when a callback or a close already settled the load (the game may call back
    /// inside the request itself), so a settled load never re-holds its callbacks.</summary>
    public void Requested(object[] keepAlive)
    {
        if (!_settled) _keepAlive = keepAlive;
    }

    /// <summary>The game's pre-create callback handed over the model being made.</summary>
    public void Created(object model) => _model ??= model;

    /// <summary>The load finished. <paramref name="target"/> = the model to make posable (Ready) or to remove now
    /// (Recycle — already forgotten here); null when the callback is ignored.</summary>
    public NpcLoadStep Loaded(object? model, out object? target)
    {
        Settle();
        var step = _gate.OnLoad();
        target = null;
        if (step == NpcLoadStep.Ignore) return step;
        target = _model ??= model;
        if (step == NpcLoadStep.Ready) _ready = true;
        else _model = null;
        return step;
    }

    /// <summary>The load failed: everything is forgotten unless a model had already become posable.</summary>
    public NpcLoadStep Failed()
    {
        Settle();
        var step = _gate.OnError();
        if (!_ready) _model = null;
        return step;
    }

    /// <summary>The person is closed: returns the posable model to remove now (forgotten here), else null — a pending
    /// load's model is removed when it arrives (<see cref="Loaded"/> → Recycle).</summary>
    public object? Close()
    {
        Settle();
        if (_gate.Close() != NpcLoadStep.Recycle) return null;
        var model = _model;
        _model = null;
        return model;
    }

    private void Settle()
    {
        _settled = true;
        _keepAlive = null;
    }
}
