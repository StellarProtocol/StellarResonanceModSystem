#!/usr/bin/env python3
"""Builds the framework's English game-text table (src/Stellar.Application/Resources/GameText.en.tsv.zst).

One compact file replaces the 8.6 MB community BuffTable.json each plugin used to embed. It keeps only what plugins
display: per buff/skill id the English Name, English NameDesign (design label), Desc and Note.

Inputs (see README.md in this folder for where they live):
  --buff-en      community English BuffTable dump (keyed by id; Id/Name/NameDesign/Desc/Note). English NameDesign/Note.
  --buff-table   extracted BuffTable.json of the CURRENT game build (English Name/Desc for real rows, Chinese NameDesign).
  --skill-table  extracted SkillTable.json of the current build (same shape).
  --talent-table extracted TalentTable.json of the current build (Chinese `Des` = talent name -> English `TalentName`).
  --design-tsv   `<chinese design label>\t<english>` translations (the Death Recap file).
  --out          output .tsv.zst path.
  --samples      optional: ids to print before/after for (comma separated).

Rules:
  * An extracted row with no localized name/desc carries a FILLER string instead of "" ("气刃突刺计数" for buffs,
    "场地标记01" for skills; the community dump's English filler is "Spirit Blade Thrust count"). Fillers become "".
  * Name/Desc: current build first, then the community dump (real names are never rewritten).
  * NameDesign/Note (Chinese design labels) go through the "official first, then Death Recap" chain, see translate():
      1. official terms: every Chinese term the game itself names in English (Skill/Buff NameDesign -> Name,
         Talent Des -> TalentName) is substituted longest-match-first, so labels use the in-game wording
         (聚能射击附加钢制喙 -> "Radiance Barrage adds Steel Beak");
      2. the Chinese left between those terms: Death Recap exact label, then Death Recap entries / the small
         connector GLOSSARY below for the fragments;
      3. if any Chinese is still left: the Death Recap translation of the whole label, then the community dump's
         English design name (both with official term names swapped in where their wording of a term is found);
      4. otherwise empty. Output never contains CJK text.
  * Any value still containing CJK text is dropped (the file is English only).

Format (UTF-8, zstd level 19): first line "STGT1"; then one row per id:
  <kind>\t<id>\t<name>\t<design>\t<desc>\t<note>      kind = B (buff) | S (skill)
Fields are escaped: backslash -> \\, tab -> \t, newline -> \n, carriage return -> \r. Empty field = no text.
"""
import argparse, collections, json, re, sys

import zstandard

FILLERS = {"气刃突刺计数", "Spirit Blade Thrust count", "场地标记01"}
CJK = re.compile(r"[\u3000-\u303f\u3400-\u4dbf\u4e00-\u9fff\uff00-\uffef]")
HAN = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")
PURE_HAN = re.compile(r"^[\u3400-\u4dbf\u4e00-\u9fff·]+$")

# Generic words the official tables happen to name idiosyncratically (e.g. a buff whose design label is just 减速 is
# called "Slow", another "Chill"): substituting them inside other labels gives wrong partial matches, so they are
# never official terms; the glossary / Death Recap handle them instead.
OFFICIAL_STOP = {
    "伤害", "治疗", "标记", "被动", "叠层", "减速", "加速", "眩晕", "霸体", "无敌", "召唤", "护盾", "攻击", "增伤",
    "减伤", "回复", "冷却", "免疫", "沉默", "击飞", "击退", "浮空", "倒地", "隐身", "死亡", "复活", "冰冻", "灼烧",
    "中毒", "流血", "虚弱", "易伤", "嘲讽", "移速", "攻速", "暴击", "格挡", "闪避", "持续", "效果", "触发", "状态",
    "技能", "普攻", "大招", "子弹", "爆炸", "范围", "区域", "怪物", "玩家", "队友", "目标", "自身", "能量", "充能",
    "跳跃", "冲刺", "位移", "定身", "恐惧", "睡眠", "变身", "吸血", "反伤", "回血", "加血", "扣血", "传送", "测试",
}

# Connector / suffix words used inside design labels. Death Recap wording where it has one ("Sub-Buff", "Stacks").
GLOSSARY = {
    "子BUFF": "Sub-Buff", "子buff": "Sub-Buff", "子Buff": "Sub-Buff", "叠层": "Stacks", "层数": "Stacks",
    "附加": "adds", "不消耗": "doesn't consume", "消耗": "consumes", "伤害区域": "Damage Zone", "伤害": "Damage",
    "区域": "Zone", "标记": "Marker", "被动": "Passive", "增伤": "Damage Up", "减伤": "Damage Reduction",
    "加成": "Bonus", "触发": "Trigger", "效果": "Effect", "持续": "Lasting", "冷却": "Cooldown", "减速": "Slow",
    "治疗": "Healing", "护盾": "Shield", "召唤": "Summon", "召唤物": "Summon", "爆炸": "Explosion", "监听": "Listener",
    "计数": "Counter", "判定": "Check", "命中": "Hit", "后": "After", "时": "When", "期间": "During", "强化": "Enhanced",
    "额外": "Extra", "专用": "Only", "表现": "Visual", "特效": "VFX", "预警": "Warning", "免疫": "Immunity",
    "无敌": "Invincible", "霸体": "Super Armor", "眩晕": "Stun", "回复": "Recovery", "能量": "Energy", "充能": "Charge",
    "大招": "Ultimate", "普攻": "Basic Attack", "技能": "Skill", "怪物": "Monster", "玩家": "Player", "自身": "Self",
    "目标": "Target", "队友": "Ally", "层": "Stack", "的": "", "和": "and", "与": "and", "及": "and", "或": "or",
    "不": "No", "增加": "Increase", "减少": "Reduce", "提升": "Boost", "降低": "Lower", "移除": "Remove",
}
ASCII_WORDS = {"buff": "Buff", "boss": "Boss", "npc": "NPC", "skill": "Skill", "dot": "DoT", "hot": "HoT", "cd": "CD",
               "ui": "UI", "bossui": "Boss UI", "gm": "GM", "ult": "ULT", "atk": "ATK"}
PUNCT = {"（": "(", "）": ")", "！": "!", "，": ", ", "、": ", ", "％": "%", "＋": "+", "·": " ", "【": "", "】": "",
         "《": "", "》": "", "“": '"', "”": '"', "？": "?", "；": "; "}
SEP = re.compile(r"\s*(?:[-—－_~～]+|[：:])\s*")


def clean(v):
    if not isinstance(v, str):
        return ""
    v = v.strip()
    if not v or v in FILLERS or CJK.search(v):
        return ""
    return v


def esc(v):
    return v.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r")


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


def build_official(sources, log):
    """Chinese term -> official English name. `sources` = (priority, rows, zh_field, en_field)."""
    votes = collections.defaultdict(collections.Counter)
    for prio, rows, zf, ef in sources:
        for r in rows:
            zh, en = (r.get(zf) or "").strip(), clean(r.get(ef))
            if not en or not PURE_HAN.match(zh) or zh in OFFICIAL_STOP:
                continue
            han = len(HAN.findall(zh))
            # Term-sized only. A long internal label whose row got a short summary name (织梦师眩晕超级加长版 ->
            # "Stun", 岩蛇本丢炸弹 -> "Bomb") is a description, not a term, and would erase meaning when substituted.
            if han < 2 or han > 8 or (han >= 5 and " " not in en):
                continue
            if re.search(r"(?i)\btest|^attr", en):
                continue
            votes[zh][(en, prio)] += 1
    out, ambiguous = {}, 0
    for zh, c in votes.items():
        by_en = collections.Counter()
        best_prio = {}
        for (en, prio), n in c.items():
            by_en[en] += n
            best_prio[en] = min(prio, best_prio.get(en, 99))
        ranked = sorted(by_en.items(), key=lambda kv: (-kv[1], best_prio[kv[0]], kv[0]))
        if len(ranked) > 1:
            ambiguous += 1
            if ranked[0][1] == ranked[1][1] and best_prio[ranked[0][0]] == best_prio[ranked[1][0]]:
                continue                                # a true tie: no deterministic winner, leave it out
        out[zh] = ranked[0][0]
    log["official terms"] = len(out)
    log["official ambiguous (dominant kept / tie dropped)"] = ambiguous
    return out


class Translator:
    def __init__(self, official, design, community_by_label):
        self.official = official
        self.design = design
        self.frag = dict(GLOSSARY)
        for zh, en in design.items():                   # short Death Recap entries double as fragments
            if PURE_HAN.match(zh) and len(zh) <= 6 and clean(en):
                self.frag.setdefault(zh, en)
        self.max_off = max(map(len, official), default=0)
        self.max_frag = max(map(len, self.frag), default=0)
        # Other wordings of each official term (Death Recap / community), to swap into whole-label fallbacks.
        self.renderings = collections.defaultdict(set)
        for zh, en in official.items():
            for alt in (design.get(zh), community_by_label.get(zh)):
                alt = clean(alt)
                if alt and alt.lower() != en.lower() and len(alt) >= 4:
                    self.renderings[zh].add(alt)

    def _run(self, run, used):
        """A CJK/ASCII word without separators -> English pieces, or None if some Chinese is left."""
        out, i, n = [], 0, len(run)
        # Pass 1: official terms, longest match first. Everything else stays as raw text for pass 2.
        pieces, buf = [], ""
        while i < n:
            hit = None
            if HAN.match(run[i]):
                for L in range(min(self.max_off, n - i), 1, -1):
                    if run[i:i + L] in self.official:
                        hit = run[i:i + L]
                        break
            if hit:
                if buf:
                    pieces.append((False, buf)); buf = ""
                pieces.append((True, self.official[hit])); used["official"] += 1
                i += len(hit)
            else:
                buf += run[i]; i += 1
        if buf:
            pieces.append((False, buf))
        # Pass 2: the remaining text: exact Death Recap / glossary entry, else fragment decomposition.
        for is_off, text in pieces:
            if is_off:
                out.append(text)
                continue
            exact = clean(self.design.get(text)) or self.frag.get(text)
            if exact is not None and (exact or text in GLOSSARY):
                out.append(exact); used["recap"] += 1
                continue
            sub = self._decompose(text, used)
            if sub is None:
                return None
            out.extend(sub)
        return out

    def _decompose(self, text, used):
        out, i, n = [], 0, len(text)
        while i < n:
            ch = text[i]
            if re.match(r"[A-Za-z0-9.%+]", ch):
                m = re.match(r"[A-Za-z0-9.%+]+", text[i:]).group(0)
                out.append(ASCII_WORDS.get(m.lower(), m)); i += len(m)
                continue
            if ch in PUNCT:
                out.append(PUNCT[ch]); i += 1
                continue
            for L in range(min(self.max_frag, n - i), 0, -1):
                f = text[i:i + L]
                if f in self.frag:
                    out.append(self.frag[f]); used["recap"] += 1; i += L
                    break
            else:
                return None
        return out

    def _swap_terms(self, zh, en):
        for term, official in self.official.items():
            if term in zh:
                for alt in self.renderings.get(term, ()):
                    en = re.sub(r"(?i)(?<![A-Za-z])" + re.escape(alt) + r"(?![A-Za-z])", official, en)
        return en

    def translate(self, zh, community_en):
        """-> (english, how), how = official | recap-exact | recap-fragments | recap-whole | community | none."""
        zh = (zh or "").strip() if isinstance(zh, str) else ""
        if not zh:
            return clean(community_en), ("community" if clean(community_en) else "none")
        used = {"official": 0, "recap": 0}
        segs = []
        for seg in SEP.split(zh):
            if not seg:
                continue
            words = []
            for w in seg.split():
                p = self._run(w, used)
                if p is None:
                    words = None
                    break
                words.append(" ".join(x for x in p if x))
            if words is None:
                segs = None
                break
            segs.append(" ".join(w for w in words if w))
        if segs is not None:
            en = tidy(" - ".join(s for s in segs if s))
            if en and not CJK.search(en):
                return en, ("official" if used["official"] else
                            "recap-exact" if clean(self.design.get(zh)) == en else "recap-fragments")
        whole = clean(self.design.get(zh))
        if whole:
            return self._swap_terms(zh, whole), "recap-whole"
        com = clean(community_en)
        if com:
            return self._swap_terms(zh, com), "community"
        return "", "none"


def tidy(s):
    s = re.sub(r"\s+", " ", s)
    s = re.sub(r"\s+([),!?%;])", r"\1", s).replace("( ", "(")
    s = s.strip(" -")
    return s[:1].upper() + s[1:]


def main():
    ap = argparse.ArgumentParser()
    for a in ("--buff-en", "--buff-table", "--skill-table", "--talent-table", "--design-tsv", "--out"):
        ap.add_argument(a, required=True)
    ap.add_argument("--samples", default="")
    args = ap.parse_args()

    buff_en = json.load(open(args.buff_en, encoding="utf-8"))
    buff_cn = json.load(open(args.buff_table, encoding="utf-8"))
    skill_cn = json.load(open(args.skill_table, encoding="utf-8"))
    talent = json.load(open(args.talent_table, encoding="utf-8"))
    design = load_tsv(args.design_tsv)

    log = {}
    buff_rows = [dict(r, Name=first(clean(r.get("Name")), clean(buff_en.get(k, {}).get("Name"))))
                 for k, r in buff_cn.items()]
    official = build_official([(0, skill_cn.values(), "NameDesign", "Name"),
                               (1, talent.values(), "Des", "TalentName"),
                               (2, buff_rows, "NameDesign", "Name")], log)
    community_by_label = {}
    for k, r in buff_cn.items():
        lab, en = (r.get("NameDesign") or "").strip(), clean(buff_en.get(k, {}).get("NameDesign"))
        if lab and en:
            community_by_label.setdefault(lab, en)
    tr = Translator(official, design, community_by_label)

    how = collections.Counter()
    labels = {}

    def design_of(zh, community_en):
        en, h = tr.translate(zh, community_en)
        if zh or community_en:
            how[h] += 1
        return en

    rows = []
    stats = {"B": [0, 0], "S": [0, 0]}   # rows, rows with design
    for sid in sorted(set(buff_en) | set(buff_cn), key=int):
        en, cur = buff_en.get(sid, {}), buff_cn.get(sid, {})
        name = first(clean(cur.get("Name")), clean(en.get("Name")))
        dsgn = design_of(cur.get("NameDesign"), en.get("NameDesign"))
        desc = first(clean(cur.get("Desc")), clean(en.get("Desc")))
        note_zh = cur.get("Note") if isinstance(cur.get("Note"), str) else ""
        note = dsgn if note_zh and note_zh.strip() == (cur.get("NameDesign") or "").strip() \
            else design_of(note_zh, en.get("Note"))
        labels[("B", int(sid))] = (cur.get("NameDesign"), first(clean(en.get("NameDesign")),
                                                                 clean(design.get((cur.get("NameDesign") or "").strip()))), dsgn)
        if name or dsgn or desc or note:
            rows.append(("B", int(sid), name, dsgn, desc, note))
            stats["B"][0] += 1; stats["B"][1] += bool(dsgn)
    for sid in sorted(skill_cn, key=int):
        cur = skill_cn[sid]
        name, desc = clean(cur.get("Name")), clean(cur.get("Desc"))
        dsgn = design_of(cur.get("NameDesign"), "")
        labels[("S", int(sid))] = (cur.get("NameDesign"), clean(design.get((cur.get("NameDesign") or "").strip())), dsgn)
        if name or dsgn or desc:
            rows.append(("S", int(sid), name, dsgn, desc, ""))
            stats["S"][0] += 1; stats["S"][1] += bool(dsgn)

    text = "STGT1\n" + "".join(f"{k}\t{i}\t{esc(n)}\t{esc(d)}\t{esc(ds)}\t{esc(no)}\n" for k, i, n, d, ds, no in rows)
    raw = text.encode("utf-8")
    packed = zstandard.ZstdCompressor(level=19).compress(raw)
    with open(args.out, "wb") as f:
        f.write(packed)
    for k, v in log.items():
        print(f"{k}: {v}", file=sys.stderr)
    print("design/note labels by source: " + ", ".join(f"{k} {v}" for k, v in how.most_common()), file=sys.stderr)
    print(f"buff rows {stats['B'][0]} (design {stats['B'][1]}), skill rows {stats['S'][0]} (design {stats['S'][1]}); "
          f"raw {len(raw):,} B -> zstd {len(packed):,} B", file=sys.stderr)
    for s in filter(None, args.samples.split(",")):
        kind, _, i = s.partition(":") if ":" in s else ("B", "", s)
        zh, before, after = labels.get((kind, int(i)), ("", "", ""))
        print(f"{kind}\t{i}\t{zh}\t{before}\t{after}", file=sys.stderr)


if __name__ == "__main__":
    main()
