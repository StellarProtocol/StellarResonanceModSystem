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
| `--curated-tsv` | `tools/gametext/s4-design-names.en.tsv` (this folder; repeatable, later files win) | hand-curated whole-label English for the S4 labels nothing else covers (Psychoscope factors, S4 gear sets, Lampsis / Wirda / Boyce dungeon mechanics, vehicles, toys) |
| `--mod-effect-table` | `Knowledge Base/Resources/<build>/tables/ModEffectTable.json` | module effects: the buff each effect grants gets the official `EffectName` (精英打击 → "Elite Strike") |
| `--season-talent-template` | `Knowledge Base/Resources/<build>/tables/SeasonTalentTemplateTable.json` (`SeasonTalentEffectOrdinaryTable.json` / `SeasonTalentEffectAdvancedTable.json` are read from the same folder) | Psychoscope effects: the official `TemplateName` for each root 【X】, the node `Name` for each node buff, and the family / node terms |
| `--prev-buff-table` / `--prev-skill-table` | `Knowledge Base/Resources/<previous build>/tables/{Buff,Skill}Table.json` | the previous build's English Name/Desc, kept when the current build only has a filler for the SAME row (same NameDesign) |

`<build>` = the folder matching the installed game binary (`release_4.7` = Season 4 when this was generated,
2026-11-19; previous build `release_3.7_2`).

Merge rules and the file format are in the script's docstring. In short: extraction fillers (`气刃突刺计数`,
`Spirit Blade Thrust count`, `场地标记01`) become empty; Name/Desc prefer the current build (real names are never
rewritten); anything still containing CJK text is dropped.

## Design labels: official first, then Death Recap

`NameDesign` (and `Note`) are Chinese design labels. Each one goes through this chain:

0. **Official whole labels, then curated.** A label whose buff is granted by a module effect or a Psychoscope node
   takes that row's official English as a whole (`梦幻箭雨：箭雨·精通箭矢` → "Arrow Rain: Arrow Mastery", root
   `【梦幻箭雨】` → "Dreamlike Arrow Rain"). When grants disagree the newest season wins: S3 template 3 "Phantom
   Arrow" and S4 template 16 "Dreamlike Arrow Rain" share the root buff 3002410. Next, an exact entry in a
   `--curated-tsv` file. The Psychoscope family and node terms (梦幻箭雨, 幻灭·蚀痕, …) also join the official terms
   of step 1 (same size / stoplist rules), so helper rows read `——幻灭崩界cd` → "Disillusion Worldbreak CD".
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

Stats when generated (2026-11-19, `release_4.7`, 17,640 design/note labels): official whole label 204 · curated
963 · official terms 1,510 · Death Recap exact 7,139 · Death Recap fragments 459 · Death Recap whole-label fallback
4,612 · community fallback 1,179 · none 1,574. 1,203 official terms (6 ambiguous) + 88 Psychoscope terms;
199 official whole labels; 945 curated labels.

S4 buffs whose game `Name` is a filler (10,106 of 11,786) and that now get an English name: 9,647. The 2.34.0 table
(built from `release_3.7_2`) gave 8,555; the old generator on `release_4.7` inputs gave 8,595. Still unnamed: 391
tower-defense event rows (830xxx/831xxx, left out on purpose) and 68 rows with no design label at all. Against the 2.34.0
table no row lost a name except three skill ids S4 reused for new labels (now curated). The previous-build fallback
keeps 0 rows for this build: the 14 Module buffs that "lost" their S3 names had their LABEL changed (【豪胆】 →
精英打击), so they are named by `--mod-effect-table` instead ("Boldness" was the community wording of the old label;
in game the module is "Elite Strike").

## Regenerate

Needs Python 3 with `zstandard` (on this workspace: activate conda first). From the `Mod/` folder:

```
python tools-path/build_gametext.py \
  --buff-en      "Knowledge Base/Resources/release_3.7/Translated/BuffTable.json" \
  --buff-table   "Knowledge Base/Resources/release_4.7/tables/BuffTable.json" \
  --skill-table  "Knowledge Base/Resources/release_4.7/tables/SkillTable.json" \
  --talent-table "Knowledge Base/Resources/release_4.7/tables/TalentTable.json" \
  --design-tsv   StellarExperimentPlugin/Data/DeathRecapDesignNames.en.tsv \
  --curated-tsv  tools-path/s4-design-names.en.tsv \
  --prev-buff-table  "Knowledge Base/Resources/release_3.7_2/tables/BuffTable.json" \
  --prev-skill-table "Knowledge Base/Resources/release_3.7_2/tables/SkillTable.json" \
  --mod-effect-table "Knowledge Base/Resources/release_4.7/tables/ModEffectTable.json" \
  --season-talent-template "Knowledge Base/Resources/release_4.7/tables/SeasonTalentTemplateTable.json" \
  --out          StellarResonanceModSystem/src/Stellar.Application/Resources/GameText.en.tsv.zst
```

(`tools-path` = this folder.) It prints the term count, the per-source label stats above, row counts and sizes
(`--samples 2203650,S:2234,...` also prints before/after for those ids). Then run the `GameTextServiceTests` — they
check real rows (2203291, 2203521, 55240, 2203650, 2203660, 3002410, 3002490, 2300020, 4501, skill 2234) against the
embedded file.

On a new game build: point the current-build inputs at the new folder and `--prev-*` at the old one, then list the
filler-named buffs that still come out unnamed and add the player-relevant ones to a curated TSV. Official skill
wording for a buff is in `AttrDescription.json` (the row's `TipsDescription` id); boss / NPC names come from
`ModelTable.ModelDesc` (Chinese) → `MonsterTable.Name` via the shared `ModelID`.
