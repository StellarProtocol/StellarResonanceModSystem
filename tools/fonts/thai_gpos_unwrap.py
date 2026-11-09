"""Rewrite a shipped Stellar UI Thai face so its Thai `mark` / `mkmk` GPOS lookups are PLAIN lookups (type 4 / 6),
not Extension lookups (type 9).

Why: the faces are Noto Sans + Noto Sans Thai merges. The merged Latin GPOS (large kern) pushes the Thai lookups
past the 64 KiB offset limit, so the merge wraps them in Extension lookups. TextMeshPro (3.2-pre, Unity 2022.3)
gets its mark-to-base / mark-to-mark records from Unity's native TextCore FontEngine. Our Thai stacking failed in game,
and that native reader may not follow Extension lookups. Moving the Thai positioning lookups to the FRONT of the
LookupList keeps their subtables within 16-bit offsets, so fontTools no longer needs to wrap them. The Latin
lookups behind them may be wrapped instead, which is harmless: Latin UI text is precomposed and needs no mark
attachment.

Lookup ORDER changes only position-lookup application order. Thai marks then attach before Thai kern, and
mark/mkmk positions are relative to the attachment, so a shaper still ends with the same result.

Usage (conda python with fontTools):  python thai_gpos_unwrap.py <in.ttf> <out.ttf>
Idempotent: a face whose Thai mark/mkmk lookups are already plain is rewritten with the same layout.
"""
import io
import sys

from fontTools.ttLib import TTFont


def thai_mark_lookups(gpos):
    out = []
    for sr in gpos.ScriptList.ScriptRecord:
        if sr.ScriptTag != "thai":
            continue
        for fi in sr.Script.DefaultLangSys.FeatureIndex:
            fr = gpos.FeatureList.FeatureRecord[fi]
            if fr.FeatureTag in ("mark", "mkmk"):
                out += [i for i in fr.Feature.LookupListIndex if i not in out]
    return out


def unwrap_extensions(gpos):
    for lk in gpos.LookupList.Lookup:
        if lk.LookupType == 9:
            lk.SubTable = [st.ExtSubTable for st in lk.SubTable]
            lk.LookupType = lk.SubTable[0].LookupType
            lk.SubTableCount = len(lk.SubTable)


def move_to_front(gpos, first):
    lookups = gpos.LookupList.Lookup
    order = first + [i for i in range(len(lookups)) if i not in first]
    remap = {old: new for new, old in enumerate(order)}
    gpos.LookupList.Lookup = [lookups[i] for i in order]
    for fr in gpos.FeatureList.FeatureRecord:
        fr.Feature.LookupListIndex = [remap[i] for i in fr.Feature.LookupListIndex]
    # Contextual lookups (type 7/8) reference other lookups by index through PosLookupRecords.
    seen = set()

    def walk(o):
        if id(o) in seen or isinstance(o, (str, bytes, int, float)):
            return
        seen.add(id(o))
        if hasattr(o, "LookupListIndex") and hasattr(o, "SequenceIndex"):
            o.LookupListIndex = remap[o.LookupListIndex]
            return
        if isinstance(o, (list, tuple)):
            for x in o:
                walk(x)
        elif hasattr(o, "__dict__"):
            for v in vars(o).values():
                walk(v)

    for lk in gpos.LookupList.Lookup:
        walk(lk.SubTable)


def main(src, dst):
    font = TTFont(src)
    gpos = font["GPOS"].table
    thai = thai_mark_lookups(gpos)
    if not thai:
        raise SystemExit("no Thai mark/mkmk lookups found")
    unwrap_extensions(gpos)
    move_to_front(gpos, thai)
    buf = io.BytesIO()
    font.save(buf)
    buf.seek(0)
    check = TTFont(buf)["GPOS"].table
    types = {i: check.LookupList.Lookup[i].LookupType for i in thai_mark_lookups(check)}
    if any(t == 9 for t in types.values()):
        raise SystemExit(f"Thai mark lookups still wrapped in Extension: {types}")
    with open(dst, "wb") as f:
        f.write(buf.getvalue())
    print(f"{dst}: Thai mark/mkmk lookups {types}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
