using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class GameDataCombatService : IGameDataCombat
{
    // Each cache is a Dictionary<int, TInfo> read lock-free via Volatile.Read
    // and replaced atomically via Volatile.Write. Build happens on the game
    // thread; reads happen on any thread.
    private IReadOnlyDictionary<int, SkillInfo>?            _skills;
    // Leveled-skill-id (baseSkillId*100+level) -> base-skill-id. Built from
    // SkillFightLevelTableBase by the probe; used to resolve damage-event skill
    // ids that are not direct keys in _skills (e.g. 2031104 -> 20311).
    private IReadOnlyDictionary<int, int>?                 _skillLevelToBase;
    private IReadOnlyDictionary<int, BuffInfo>?             _buffs;
    private IReadOnlyDictionary<int, ProfessionInfo>?       _professions;
    private IReadOnlyDictionary<int, TalentInfo>?           _talents;
    private IReadOnlyDictionary<int, AttributeInfo>?        _attributes;
    private IReadOnlyDictionary<int, AttributeProfileInfo>? _attributeProfiles;
    private IReadOnlyDictionary<int, DamageAttrInfo>?       _damageAttrs;

    // Direct hit wins (base ids resolve unchanged). On a miss, resolve a leveled
    // id (baseSkillId*100+level) to its base via the SkillFightLevelTableBase map
    // and return the base skill's info — so skill names resolve for damage events
    // that carry a leveled skill_level_id rather than a base id.
    public SkillInfo? GetSkill(int id)
    {
        var skills = Volatile.Read(ref _skills);
        if (TryGet(skills, id) is { } direct) return direct;

        var levelToBase = Volatile.Read(ref _skillLevelToBase);
        if (levelToBase is not null && levelToBase.TryGetValue(id, out var baseId) && baseId != 0 && baseId != id)
        {
            return TryGet(skills, baseId);
        }
        return null;
    }
    public BuffInfo?             GetBuff(int id)             => TryGet(Volatile.Read(ref _buffs), id);
    public ProfessionInfo?       GetProfession(int id)       => TryGet(Volatile.Read(ref _professions), id);
    public TalentInfo?           GetTalent(int id)           => TryGet(Volatile.Read(ref _talents), id);
    // Merge policy: live (localized game-table) row wins for Name/ShortName/IconPath/Group/NumType when it
    // has real values; the generated catalog backfills gaps and is ALWAYS authoritative for EnumName.
    // Live NumType is -1 or >= 0 by construction (the probe's FightAttr join never emits -2), so "< 0"
    // uniformly means "no FightAttr row" on the live side.
    public AttributeInfo? GetAttribute(int id)
    {
        var live = TryGet(Volatile.Read(ref _attributes), id);
        var cat  = AttrCatalog.TryGet(id);
        if (live is { } l)
        {
            if (cat is not { } c) return l;
            return l with
            {
                Name      = string.IsNullOrEmpty(l.Name) ? c.Name : l.Name,
                ShortName = string.IsNullOrEmpty(l.ShortName) ? c.ShortName : l.ShortName,
                EnumName  = c.EnumName,
                Group     = l.Group == AttributeGroup.Unknown ? c.Group : l.Group,
                NumType   = l.NumType >= 0 ? l.NumType : c.NumType,
            };
        }
        return cat;
    }
    public AttributeProfileInfo? GetAttributeProfile(int id)
    {
        // Cache is keyed by the EAttrType BASE attr id (e.g. AttrMaxHp=11320).
        // Plugins frequently look up via the TOTAL variant (AttrMaxHpTotal=11321),
        // which is base+1 throughout EAttrType. Try the requested id, then fall
        // back to (id - 1) so callers get a hit for both Base and Total forms.
        var cache = Volatile.Read(ref _attributeProfiles);
        if (cache is null) return null;
        if (cache.TryGetValue(id, out var direct)) return direct;
        if (cache.TryGetValue(id - 1, out var totalFallback)) return totalFallback;
        return null;
    }

    public DamageAttrInfo?       GetDamageAttr(int id)       => TryGet(Volatile.Read(ref _damageAttrs), id);

    internal void LoadSkills(IReadOnlyDictionary<int, SkillInfo> cache)
        => Volatile.Write(ref _skills, cache);
    internal void LoadSkillLevelToBase(IReadOnlyDictionary<int, int> cache)
        => Volatile.Write(ref _skillLevelToBase, cache);
    internal void LoadBuffs(IReadOnlyDictionary<int, BuffInfo> cache)
        => Volatile.Write(ref _buffs, cache);
    internal void LoadProfessions(IReadOnlyDictionary<int, ProfessionInfo> cache)
        => Volatile.Write(ref _professions, cache);
    internal void LoadTalents(IReadOnlyDictionary<int, TalentInfo> cache)
        => Volatile.Write(ref _talents, cache);
    internal void LoadAttributes(IReadOnlyDictionary<int, AttributeInfo> cache)
        => Volatile.Write(ref _attributes, cache);
    internal void LoadAttributeProfiles(IReadOnlyDictionary<int, AttributeProfileInfo> cache)
        => Volatile.Write(ref _attributeProfiles, cache);
    internal void LoadDamageAttrs(IReadOnlyDictionary<int, DamageAttrInfo> cache)
        => Volatile.Write(ref _damageAttrs, cache);

    private static T? TryGet<T>(IReadOnlyDictionary<int, T>? cache, int id) where T : struct
    {
        if (cache is null) return null;
        return cache.TryGetValue(id, out var info) ? info : null;
    }
}
