# English game text (`IGameText` data)

`build_gametext.py` builds `src/Stellar.Application/Resources/GameText.en.tsv.zst`, the table behind
`IPluginServices.GameText`. It is embedded in `Stellar.Application.dll` (LogicalName
`Stellar.Application.GameText.en.tsv.zst`), so there is no extra file to deploy.

## Sources

The inputs live in the mod workspace, not in this repo (paths relative to the workspace `Mod/` folder):

| Input | Path | Gives |
|---|---|---|
| `--buff-en` | `Knowledge Base/Resources/release_3.7/Translated/BuffTable.json` (same file as `StellarCooldownBarPlugin/Resources/BuffTable.json`) | community English BuffTable dump (the file CooldownBar/TargetLens embed): English `NameDesign` + `Note` (last-resort fallback) |
| `--buff-table` | `Knowledge Base/Resources/<build>/tables/BuffTable.json` | the current build's extracted table: English `Name`/`Desc` for real rows, Chinese `NameDesign`, current id set |
| `--skill-table` | `Knowledge Base/Resources/<build>/tables/SkillTable.json` | same for skills |
| `--talent-table` | `Knowledge Base/Resources/<build>/tables/TalentTable.json` | Chinese `Des` → official `TalentName` (official terms only, e.g. 钢制喙 → "Steel Beak") |
| `--design-tsv` | `StellarExperimentPlugin/Data/DeathRecapDesignNames.en.tsv` | `<chinese design label>\t<english>` for 11.6k labels — skills and buffs the community dump lacks |

`<build>` = the folder matching the installed game binary (`release_3.7_2` when this was generated, 2026-11-17).

Merge rules and the file format are in the script's docstring. In short: extraction fillers (`气刃突刺计数`,
`Spirit Blade Thrust count`, `场地标记01`) become empty; Name/Desc prefer the current build (real names are never
rewritten); anything still containing CJK text is dropped.

## Design labels: official first, then Death Recap

`NameDesign` (and `Note`) are Chinese design labels. Each one goes through this chain:

1. **Official terms.** A Chinese → official English dictionary from every SkillTable / BuffTable row (`NameDesign` →
   `Name`) and TalentTable row (`Des` → `TalentName`) that has a real English name. Only term-sized keys: pure Chinese,
   2–8 characters, not a generic word (`OFFICIAL_STOP`: 伤害, 减速, 标记, …), and not a long label whose row got a
   one-word summary name (织梦师眩晕超级加长版 → "Stun"). Same Chinese with different names → the most common name
   (ties between equal-priority sources are dropped). Terms are replaced longest-match-first:
   聚能射击附加钢制喙 → **Radiance Barrage** 附加 **Steel Beak**.
2. **Death Recap for what is left.** Each remaining Chinese piece: the Death Recap file's exact entry, else a
   decomposition into Death Recap short entries and the connector `GLOSSARY` in the script (子BUFF → "Sub-Buff",
   叠层 → "Stacks", 附加 → "adds", 不消耗 → "doesn't consume", …). Label separators (`-`, `_`, `：`) become `" - "`.
   → "Radiance Barrage adds Steel Beak". A label with no official term is just its Death Recap exact entry.
3. **Fallbacks, whole label** (only when 1+2 leave Chinese): the Death Recap translation of the whole label, then
   the community dump's English design name — both with the official name swapped in where their own wording of a
   contained term appears ("Charged Shot" → "Radiance Barrage").
4. Nothing usable → empty (`GetDesignName` returns null).

Stats when generated (2026-11-17, `release_3.7_2`, 15,931 design/note labels): official 1,447 · Death Recap exact
7,160 · Death Recap fragments 353 · Death Recap whole-label fallback 4,661 · community fallback 1,171 · none 1,139.
1,145 official terms (5 ambiguous). Compared to the previous community-first file: no row lost a design name,
385 gained one, 8,436 design names changed wording.

## Regenerate

Needs Python 3 with `zstandard` (on this workspace: activate conda first). From the `Mod/` folder:

```
python tools-path/build_gametext.py \
  --buff-en     "Knowledge Base/Resources/release_3.7/Translated/BuffTable.json" \
  --buff-table  "Knowledge Base/Resources/release_3.7_2/tables/BuffTable.json" \
  --skill-table "Knowledge Base/Resources/release_3.7_2/tables/SkillTable.json" \
  --talent-table "Knowledge Base/Resources/release_3.7_2/tables/TalentTable.json" \
  --design-tsv  StellarExperimentPlugin/Data/DeathRecapDesignNames.en.tsv \
  --out         StellarResonanceModSystem/src/Stellar.Application/Resources/GameText.en.tsv.zst
```

(`tools-path` = this folder.) It prints the term count, the per-source label stats above, row counts and sizes
(`--samples 2203650,S:2234,...` also prints before/after for those ids). Then run the `GameTextServiceTests` — they
check real rows (2203291, 2203521, 55240, 2203650, 2203660, 4501, skill 2234) against the embedded file.
