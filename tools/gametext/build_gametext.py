#!/usr/bin/env python3
"""Builds the framework's English game-text table (src/Stellar.Application/Resources/GameText.en.tsv.zst).

One compact file replaces the 8.6 MB community BuffTable.json each plugin used to embed. It keeps only what plugins
display: per buff/skill id the English Name, English NameDesign (design label), Desc and Note.

Inputs (see README.md in this folder for where they live):
  --buff-en     community English BuffTable dump (keyed by id; Id/Name/NameDesign/Desc/Note). English NameDesign/Note.
  --buff-table  extracted BuffTable.json of the CURRENT game build (English Name/Desc for real rows, Chinese NameDesign).
  --skill-table extracted SkillTable.json of the current build (same shape).
  --design-tsv  `<chinese design label>\t<english>` translations (covers skills + buffs the community dump lacks).
  --out         output .tsv.zst path.

Rules:
  * An extracted row with no localized name/desc carries a FILLER string instead of "" ("气刃突刺计数" for buffs,
    "场地标记01" for skills; the community dump's English filler is "Spirit Blade Thrust count"). Fillers become "".
  * Name/Desc: current build first, then the community dump. NameDesign/Note: community dump first (the wording
    plugins already show), then the design-label translation of the current build's Chinese label.
  * Any value still containing CJK text is dropped (the file is English only).

Format (UTF-8, zstd level 19): first line "STGT1"; then one row per id:
  <kind>\t<id>\t<name>\t<design>\t<desc>\t<note>      kind = B (buff) | S (skill)
Fields are escaped: backslash -> \\, tab -> \t, newline -> \n, carriage return -> \r. Empty field = no text.
"""
import argparse, json, re, sys

import zstandard

FILLERS = {"气刃突刺计数", "Spirit Blade Thrust count", "场地标记01"}
CJK = re.compile(r"[\u3000-\u303f\u3400-\u4dbf\u4e00-\u9fff\uff00-\uffef]")


def clean(v):
    if not isinstance(v, str):
        return ""
    v = v.strip()
    if not v or v in FILLERS or CJK.search(v):
        return ""
    return v


def esc(v):
    return v.replace("\\", "\\\\").replace("\t", "\t").replace("\n", "\n").replace("\r", "\r")


def load_tsv(path):
    m = {}
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if "\t" in line:
                zh, en = line.split("\t", 1)
                m[zh.strip()] = en.strip()
    return m


def first(*vals):
    for v in vals:
        if v:
            return v
    return ""


def main():
    ap = argparse.ArgumentParser()
    for a in ("--buff-en", "--buff-table", "--skill-table", "--design-tsv", "--out"):
        ap.add_argument(a, required=True)
    args = ap.parse_args()

    buff_en = json.load(open(args.buff_en, encoding="utf-8"))
    buff_cn = json.load(open(args.buff_table, encoding="utf-8"))
    skill_cn = json.load(open(args.skill_table, encoding="utf-8"))
    design = load_tsv(args.design_tsv)

    def tr(zh):
        return clean(design.get((zh or "").strip(), "")) if isinstance(zh, str) else ""

    rows = []
    stats = {"B": [0, 0], "S": [0, 0]}   # rows, rows with design
    for sid in sorted(set(buff_en) | set(buff_cn), key=int):
        en, cur = buff_en.get(sid, {}), buff_cn.get(sid, {})
        name = first(clean(cur.get("Name")), clean(en.get("Name")))
        dsgn = first(clean(en.get("NameDesign")), tr(cur.get("NameDesign")))
        desc = first(clean(cur.get("Desc")), clean(en.get("Desc")))
        note = first(clean(en.get("Note")), tr(cur.get("Note")))
        if name or dsgn or desc or note:
            rows.append(("B", int(sid), name, dsgn, desc, note))
            stats["B"][0] += 1; stats["B"][1] += bool(dsgn)
    for sid in sorted(skill_cn, key=int):
        cur = skill_cn[sid]
        name, dsgn, desc = clean(cur.get("Name")), tr(cur.get("NameDesign")), clean(cur.get("Desc"))
        if name or dsgn or desc:
            rows.append(("S", int(sid), name, dsgn, desc, ""))
            stats["S"][0] += 1; stats["S"][1] += bool(dsgn)

    text = "STGT1\n" + "".join(f"{k}\t{i}\t{esc(n)}\t{esc(d)}\t{esc(ds)}\t{esc(no)}\n" for k, i, n, d, ds, no in rows)
    raw = text.encode("utf-8")
    packed = zstandard.ZstdCompressor(level=19).compress(raw)
    with open(args.out, "wb") as f:
        f.write(packed)
    print(f"buff rows {stats['B'][0]} (design {stats['B'][1]}), skill rows {stats['S'][0]} (design {stats['S'][1]}); "
          f"raw {len(raw):,} B -> zstd {len(packed):,} B", file=sys.stderr)


if __name__ == "__main__":
    main()
