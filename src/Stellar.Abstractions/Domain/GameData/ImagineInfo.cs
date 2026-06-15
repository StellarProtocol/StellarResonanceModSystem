namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for one Battle Imagine (Resonance Skill): display + cooldown/charge model.</summary>
/// <param name="SkillId">The equipped skill id (from the entity's skill loadout); matches self LocalCooldowns + recognises casts.</param>
/// <param name="Name">Display name from SkillTable[SkillId].Name.</param>
/// <param name="IconAddress">ZResLoader address from SkillTable[SkillId].Icon (empty if none).</param>
/// <param name="ChargeCount">Max charges from SkillTable[SkillId].MaxEnergyChargeNum (1 = single-charge).</param>
/// <param name="RechargeMs">Per-charge recharge from SkillTable[SkillId].EnergyChargeTime in ms (used when ChargeCount > 1).</param>
/// <param name="CooldownMs">Single-cast cooldown from SkillFightLevelTable[EffectIDs[1]].PVECoolTime (sec-&gt;ms; used when ChargeCount == 1).</param>
public readonly record struct ImagineInfo(int SkillId, string Name, string IconAddress, int ChargeCount, int RechargeMs, int CooldownMs);
