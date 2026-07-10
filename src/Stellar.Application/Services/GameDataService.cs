using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed class GameDataService : IGameData
{
    // Drained 1/tick. Order matches the GameDataTableKind enum minus the eager
    // set (Skill, Buff, Profession, Attribute, Item).
    private static readonly GameDataTableKind[] DeferredOrder =
    {
        GameDataTableKind.Talent,
        GameDataTableKind.DamageAttr,
        GameDataTableKind.Equip,
        GameDataTableKind.Weapon,
        GameDataTableKind.Monster,
        GameDataTableKind.Npc,
        GameDataTableKind.Scene,
        GameDataTableKind.Map,
        GameDataTableKind.Quest,
        GameDataTableKind.Dungeon,
        GameDataTableKind.Activity,
        GameDataTableKind.Achievement,
        GameDataTableKind.Title,
        GameDataTableKind.Award,
        GameDataTableKind.EquipRow,
        GameDataTableKind.EquipAttrLib,
        GameDataTableKind.EquipSchoolAttrLib,
        GameDataTableKind.EquipEnchantItem,
        GameDataTableKind.EquipPart,
    };

    private readonly GameDataCombatService    _combat    = new GameDataCombatService();
    private readonly GameDataInventoryService _inventory = new GameDataInventoryService();
    private readonly GameDataEquipService     _equip     = new GameDataEquipService();
    private readonly GameDataWorldService     _world;
    private readonly GameDataProgressService  _progress  = new GameDataProgressService();

    public GameDataService(CombatEntityTracker entityTracker)
    {
        _world = new GameDataWorldService(entityTracker
            ?? throw new System.ArgumentNullException(nameof(entityTracker)));
    }

    private bool _isAvailable;
    private int _deferredIndex;  // next position in DeferredOrder

    public bool IsAvailable => Volatile.Read(ref _isAvailable);

    public IGameDataCombat    Combat    => _combat;
    public IGameDataInventory Inventory => _inventory;
    public IGameDataEquip     Equip     => _equip;
    public IGameDataWorld     World     => _world;
    public IGameDataProgress  Progress  => _progress;

    /// <summary>
    /// Called once on the game thread at HotUpdateReady. On success, populates
    /// the eager caches and flips IsAvailable to true.
    /// </summary>
    internal void LoadEager(IGameDataProbe probe)
    {
        if (!probe.TryLoadEager(out var snapshot))
        {
            Volatile.Write(ref _isAvailable, false);
            return;
        }

        _combat.LoadSkills(snapshot.Skills);
        _combat.LoadSkillLevelToBase(snapshot.SkillLevelToBase);
        _combat.LoadBuffs(snapshot.Buffs);
        _combat.LoadProfessions(snapshot.Professions);
        _combat.LoadAttributes(snapshot.Attributes);
        _combat.LoadAttributeProfiles(snapshot.AttributeProfiles);
        _inventory.LoadItems(snapshot.Items);

        Volatile.Write(ref _isAvailable, true);
    }

    /// <summary>True once every deferred table has been drained. Host polls this instead of
    /// duplicating the table count (a hardcoded count silently skipped newly added tables).</summary>
    internal bool DeferredComplete => _deferredIndex >= DeferredOrder.Length;

    /// <summary>
    /// Called per Game.Update tick on the game thread. Loads the next deferred
    /// table from the queue. No-op once the queue is empty.
    /// </summary>
    internal void DrainDeferred(IGameDataProbe probe)
    {
        if (DeferredComplete)
        {
            return;
        }

        var kind = DeferredOrder[_deferredIndex];
        _deferredIndex++;

        if (!probe.TryLoadOne(kind, out var cache))
        {
            return; // Failure already logged in Infrastructure; skip and continue next tick.
        }

        DispatchCache(kind, cache);
    }

    private void DispatchCache(GameDataTableKind kind, object cache)
    {
        switch (kind)
        {
            case GameDataTableKind.Talent:
                _combat.LoadTalents((IReadOnlyDictionary<int, TalentInfo>)cache); break;
            case GameDataTableKind.DamageAttr:
                _combat.LoadDamageAttrs((IReadOnlyDictionary<int, DamageAttrInfo>)cache); break;
            case GameDataTableKind.Equip:
                _inventory.LoadEquips((IReadOnlyDictionary<int, EquipInfo>)cache); break;
            case GameDataTableKind.Weapon:
                _inventory.LoadWeapons((IReadOnlyDictionary<int, WeaponInfo>)cache); break;
            case GameDataTableKind.Monster:
                _world.LoadMonsters((IReadOnlyDictionary<int, MonsterInfo>)cache); break;
            case GameDataTableKind.Npc:
                _world.LoadNpcs((IReadOnlyDictionary<int, NpcInfo>)cache); break;
            case GameDataTableKind.Scene:
                _world.LoadScenes((IReadOnlyDictionary<int, SceneInfo>)cache); break;
            case GameDataTableKind.Map:
                _world.LoadMaps((IReadOnlyDictionary<int, MapInfo>)cache); break;
            case GameDataTableKind.Quest:
                _progress.LoadQuests((IReadOnlyDictionary<int, QuestInfo>)cache); break;
            case GameDataTableKind.Dungeon:
                _progress.LoadDungeons((IReadOnlyDictionary<int, DungeonInfo>)cache); break;
            case GameDataTableKind.Activity:
                _progress.LoadActivities((IReadOnlyDictionary<int, ActivityInfo>)cache); break;
            case GameDataTableKind.Achievement:
                _progress.LoadAchievements((IReadOnlyDictionary<int, AchievementInfo>)cache); break;
            case GameDataTableKind.Title:
                _progress.LoadTitles((IReadOnlyDictionary<int, TitleInfo>)cache); break;
            case GameDataTableKind.Award:
                _progress.LoadAwards((IReadOnlyDictionary<int, AwardInfo>)cache); break;
            case GameDataTableKind.EquipRow:
                _equip.LoadEquipRows((IReadOnlyDictionary<int, EquipRowInfo>)cache); break;
            case GameDataTableKind.EquipAttrLib:
                _equip.LoadAttrLibs((IReadOnlyDictionary<int, EquipAttrLibRowData>)cache); break;
            case GameDataTableKind.EquipSchoolAttrLib:
                _equip.LoadSchoolAttrLibs((IReadOnlyDictionary<int, EquipAttrSchoolLibRowData>)cache); break;
            case GameDataTableKind.EquipEnchantItem:
                _equip.LoadEnchantItems((IReadOnlyDictionary<int, EnchantItemRowData>)cache); break;
            case GameDataTableKind.EquipPart:
                _equip.LoadSlotNames((IReadOnlyDictionary<int, string>)cache); break;
            // Eager kinds are not in DeferredOrder; treating them as unreachable.
        }
    }
}
