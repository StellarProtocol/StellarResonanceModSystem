using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Time-sliced drivers of the eager batch and the deferred queue for <see cref="PandaGameDataProbe"/>
/// (2026-11-10; why + the per-table job in <c>PandaGameDataProbe.SlicedLoad.cs</c>). The one-shot
/// <see cref="TryLoadEager"/> / <see cref="TryLoadOne"/> are these same drivers with no deadline.
/// </summary>
internal sealed partial class PandaGameDataProbe
{
    // Eager batch, in the old one-shot order. FightAttrIcon MUST precede Attribute: its job publishes the
    // icon / num-type joins that LoadAttributes reads per row. Stages run strictly in sequence and each job is
    // only CREATED when its stage starts, so that still holds when the batch spans many ticks.
    private const int EagerStageCount = 8;
    private const int StageProfession = 0, StageSkill = 1, StageSkillLevel = 2, StageBuff = 3,
        StageAttribute = 5, StageAttributeProfile = 6, StageItem = 7;   // 4 = FightAttrIcon (join only)
    private readonly object?[] _eagerResults = new object?[EagerStageCount];
    private int _eagerStage;
    private TableLoadJob? _eagerJob;

    // The deferred table in flight, and which kind it is (the caller keeps passing the same kind until done).
    private TableLoadJob? _deferredJob;
    private GameDataTableKind _deferredKind;

    private TableLoadJob CreateEagerJob(int stage) => stage switch
    {
        StageProfession       => LoadProfessions(),
        StageSkill            => LoadSkills(),
        StageSkillLevel       => LoadSkillLevelToBase(),
        StageBuff             => LoadBuffs(),
        StageAttribute        => LoadAttributes(),
        StageAttributeProfile => LoadAttributeProfiles(),
        StageItem             => LoadItems(),
        _                     => LoadFightAttrIcons(),
    };

    public GameDataStep StepEager(long deadline, out GameDataEagerSnapshot snapshot)
    {
        snapshot = default;
        // Each per-table job handles its own failures (logs once, keeps an empty/partial dictionary), so one
        // bad table does not block the others.
        while (_eagerStage < EagerStageCount)
        {
            _eagerJob ??= CreateEagerJob(_eagerStage);
            if (!_eagerJob.Step(deadline)) return GameDataStep.Pending;
            _eagerResults[_eagerStage++] = _eagerJob.Result;
            _eagerJob = null;
            // Budget spent on a finished table → start the next one on the next tick.
            if (_eagerStage < EagerStageCount && Stopwatch.GetTimestamp() >= deadline) return GameDataStep.Pending;
        }

        snapshot = new GameDataEagerSnapshot
        {
            Skills            = EagerResult<SkillInfo>(StageSkill),
            SkillLevelToBase  = EagerResult<int>(StageSkillLevel),
            Buffs             = EagerResult<BuffInfo>(StageBuff),
            Professions       = EagerResult<ProfessionInfo>(StageProfession),
            Attributes        = EagerResult<AttributeInfo>(StageAttribute),
            AttributeProfiles = EagerResult<AttributeProfileInfo>(StageAttributeProfile),
            Items             = EagerResult<ItemInfo>(StageItem),
        };
        // Re-armed so a later one-shot TryLoadEager re-reads the tables, as it always could.
        _eagerStage = 0;
        Array.Clear(_eagerResults, 0, _eagerResults.Length);
        return GameDataStep.Done;
    }

    private IReadOnlyDictionary<int, T> EagerResult<T>(int stage)
        => _eagerResults[stage] as IReadOnlyDictionary<int, T> ?? new Dictionary<int, T>();

    public GameDataStep StepOne(GameDataTableKind kind, long deadline, out object cache)
    {
        cache = null!;
        if (_deferredJob is null || _deferredKind != kind)
        {
            _deferredJob = CreateDeferredJob(kind);
            _deferredKind = kind;
            if (_deferredJob is null) return GameDataStep.Failed;
        }

        bool done;
        try { done = _deferredJob.Step(deadline); }
        catch { _deferredJob = null; throw; }   // a throwing post-process must not wedge the queue on this kind
        if (!done) return GameDataStep.Pending;

        cache = _deferredJob.Result!;
        _deferredJob = null;
        return cache is not null ? GameDataStep.Done : GameDataStep.Failed;
    }
}
