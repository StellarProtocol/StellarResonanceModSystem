namespace Stellar.Infrastructure.Game.Posing;

/// <summary>What the NPC pose model does next.</summary>
internal enum NpcLoadStep { Ignore, Recycle, Ready, Fail }

/// <summary>The generated model's load / close race (spec § 4.4): the model arrives async, and the free camera can end or
/// the person can be reset first. Exactly one removal whatever the order; a model that arrives after a close is removed at
/// once and never shown. Pure (unit-tested).</summary>
internal sealed class NpcLoadGate
{
    private bool _done, _closed, _loaded;

    public NpcLoadStep OnLoad()
    {
        if (_done) return NpcLoadStep.Ignore;
        _done = true;
        if (_closed) return NpcLoadStep.Recycle;
        _loaded = true;
        return NpcLoadStep.Ready;
    }

    public NpcLoadStep OnError()
    {
        if (_done) return NpcLoadStep.Ignore;
        _done = true;
        return _closed ? NpcLoadStep.Ignore : NpcLoadStep.Fail;
    }

    public NpcLoadStep Close()
    {
        if (_closed) return NpcLoadStep.Ignore;
        _closed = true;
        return _loaded ? NpcLoadStep.Recycle : NpcLoadStep.Ignore;
    }
}
