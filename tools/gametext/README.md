# English game text (`IGameText` data)

`build_gametext.py` builds `src/Stellar.Application/Resources/GameText.en.tsv.zst`, the table behind
`IPluginServices.GameText`. It is embedded in `Stellar.Application.dll` (LogicalName
`Stellar.Application.GameText.en.tsv.zst`), so there is no extra file to deploy.

## Sources

The inputs live in the mod workspace, not in this repo (paths relative to the workspace `Mod/` folder):

| Input | Path | Gives |
|---|---|---|
| `--buff-en` | `Knowledge Base/Resources/release_3.7/Translated/BuffTable.json` | community English BuffTable dump (the file CooldownBar/TargetLens embed): English `NameDesign` + `Note` |
| `--buff-table` | `Knowledge Base/Resources/<build>/tables/BuffTable.json` | the current build's extracted table: English `Name`/`Desc` for real rows, Chinese `NameDesign`, current id set |
| `--skill-table` | `Knowledge Base/Resources/<build>/tables/SkillTable.json` | same for skills |
| `--design-tsv` | `StellarExperimentPlugin/Data/DeathRecapDesignNames.en.tsv` | `<chinese design label>\t<english>` for 11.6k labels — skills and buffs the community dump lacks |

`<build>` = the folder matching the installed game binary (`release_3.7_2` when this was generated, 2026-11-17).

Merge rules and the file format are in the script's docstring. In short: extraction fillers (`气刃突刺计数`,
`Spirit Blade Thrust count`, `场地标记01`) become empty; Name/Desc prefer the current build; NameDesign/Note prefer
the community dump (so plugins that migrate keep the wording they already show), then the design-label TSV; anything
still containing CJK text is dropped.

## Regenerate

Needs Python 3 with `zstandard` (on this workspace: activate conda first). From the `Mod/` folder:

```
python tools-path/build_gametext.py \
  --buff-en     "Knowledge Base/Resources/release_3.7/Translated/BuffTable.json" \
  --buff-table  "Knowledge Base/Resources/release_3.7_2/tables/BuffTable.json" \
  --skill-table "Knowledge Base/Resources/release_3.7_2/tables/SkillTable.json" \
  --design-tsv  StellarExperimentPlugin/Data/DeathRecapDesignNames.en.tsv \
  --out         StellarResonanceModSystem/src/Stellar.Application/Resources/GameText.en.tsv.zst
```

(`tools-path` = this folder.) It prints row counts and sizes. Then run the `GameTextServiceTests` — they check
real rows (2203291, 2203521, 55240, 4501, skill 2234) against the embedded file.
