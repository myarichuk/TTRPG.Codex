#!/usr/bin/env python3
"""Import PF2e Remastered spells + feats from Archives of Nethys into a Codex pack.

Scope is deliberately narrow, for license reasons: only entries whose primary
source is Player Core or Player Core 2 (Paizo's ORC-licensed Remastered core
rulebooks) at common rarity. Legacy OGL content (Core Rulebook, APG, ...) is
excluded by the query itself.

Mechanics-only policy: structured facts (names, levels, traits, actions, areas,
saves, prerequisites) are emitted. Prose (``text``, ``summary``, ``markdown``) is
never copied - ``text`` is read only to regex out prerequisite/frequency/trigger
names, which are facts, not expression.

Output: packs/pf2e-remastered/{manifest.json,ATTRIBUTION.md,spells/,feats/,classes/,
ancestries/,heritages/,backgrounds/,patrons/,equipment/*.yaml}, mirrored to
plugins/pf2e-remastered/ (the directory the dev-time PluginLoader reads) unless
--no-install is given. One file per entry.

Requires network access to https://elasticsearch.aonprd.com (official Paizo-run
Archives of Nethys database).
"""

from __future__ import annotations

import argparse
import datetime
import json
import re
import shutil
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACK_ID = "pf2e-remastered"
PACK_NAME = "PF2e Remastered Core (ORC)"
SYSTEM_ID = "Pf2e"

AON_SEARCH_URL = "https://elasticsearch.aonprd.com/aon/_search"
AON_REMASTER_SOURCES = ["Player Core", "Player Core 2"]

FILE_HEADER = (
    "# Source: Pathfinder 2e Remastered (Player Core / Player Core 2) by Paizo Inc.,\n"
    "# ORC license, via the official Archives of Nethys database. Mechanics only -\n"
    "# no rulebook prose is reproduced (see ../ATTRIBUTION.md).\n"
)

TRADITION_TO_CLASSES = {
    "arcane": ["wizard", "witch"],
    "divine": ["cleric"],
    "primal": ["druid"],
    "occult": ["bard"],
}
CLASS_TRAITS = {"bard", "witch", "cleric", "druid", "wizard"}

# Class trait names observed on Player Core / Player Core 2 class feats.
KNOWN_CLASSES = {
    "fighter", "barbarian", "rogue", "monk", "cleric", "bard", "druid", "ranger",
    "swashbuckler", "champion", "sorcerer", "alchemist", "investigator", "wizard",
    "oracle", "witch", "magus", "commander", "thaumaturge", "necromancer", "animist",
    "psychic", "exemplar", "gunslinger", "summoner",
}

SECTION_END = r"(?:Trigger|Frequency|Cost|Requirements?|Prerequisites?|Range|Area|Effect|Special|---|$)"
PREREQ_RE = re.compile(r"Prerequisites?\s+(.*?)\s*" + SECTION_END, re.IGNORECASE)
FREQUENCY_RE = re.compile(r"Frequency\s+(.*?)\s*" + SECTION_END, re.IGNORECASE)
TRIGGER_RE = re.compile(r"Trigger\s+(.*?)\s*" + SECTION_END, re.IGNORECASE)
COST_RE = re.compile(r"Cost\s+(.*?)\s*" + SECTION_END, re.IGNORECASE)
SAVE_TYPE_RE = re.compile(r"fortitude|reflex|will", re.IGNORECASE)


# ---------------------------------------------------------------- YAML emitter
# Stdlib-only: no PyYAML dependency. Block style, insertion-ordered keys.

def yaml_scalar(value: object) -> str:
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "null"
    if isinstance(value, (int, float)):
        return str(value)
    text = str(value)
    if re.fullmatch(r"-?\d+(\.\d+)?", text) or text.lower() in ("true", "false", "null", "~"):
        return json.dumps(text)
    if re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_ ./+()\-]*", text) and text.strip() == text:
        return text
    return json.dumps(text)


def emit_yaml(node: object, indent: int = 0) -> list[str]:
    pad = " " * indent
    if isinstance(node, dict):
        if not node:
            return [pad + "{}"]
        lines: list[str] = []
        for key, value in node.items():
            rendered_key = yaml_scalar(key)
            if isinstance(value, (dict, list)) and value:
                lines.append(f"{pad}{rendered_key}:")
                lines.extend(emit_yaml(value, indent + 2))
            elif isinstance(value, list):
                lines.append(f"{pad}{rendered_key}: []")
            elif isinstance(value, dict):
                lines.append(f"{pad}{rendered_key}: {{}}")
            else:
                lines.append(f"{pad}{rendered_key}: {yaml_scalar(value)}")
        return lines
    if isinstance(node, list):
        lines = []
        for item in node:
            if isinstance(item, dict) and item:
                pairs = list(item.items())
                first_key, first_value = pairs[0]
                if isinstance(first_value, (dict, list)) and first_value:
                    lines.append(f"{pad}- {yaml_scalar(first_key)}:")
                    lines.extend(emit_yaml(first_value, indent + 4))
                else:
                    lines.append(f"{pad}- {yaml_scalar(first_key)}: {yaml_scalar(first_value)}")
                for key, value in pairs[1:]:
                    if isinstance(value, (dict, list)) and value:
                        lines.append(f"{pad}  {yaml_scalar(key)}:")
                        lines.extend(emit_yaml(value, indent + 4))
                    elif isinstance(value, list):
                        lines.append(f"{pad}  {yaml_scalar(key)}: []")
                    elif isinstance(value, dict):
                        lines.append(f"{pad}  {yaml_scalar(key)}: {{}}")
                    else:
                        lines.append(f"{pad}  {yaml_scalar(key)}: {yaml_scalar(value)}")
            else:
                lines.append(f"{pad}- {yaml_scalar(item)}")
        return lines
    return [pad + yaml_scalar(node)]


def write_entry(path: Path, entry: dict) -> None:
    lines = [FILE_HEADER.rstrip(), ""]
    lines.extend(emit_yaml([entry]))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


# ------------------------------------------------------------------- fetching

def fetch_json(url: str, data: bytes | None = None, retries: int = 4) -> dict:
    for attempt in range(retries):
        try:
            headers = {"User-Agent": "TTRPG.Codex-importer/1.0"}
            if data is not None:
                headers["Content-Type"] = "application/json"
            req = urllib.request.Request(url, headers=headers, data=data)
            with urllib.request.urlopen(req, timeout=60) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception:
            if attempt == retries - 1:
                raise
            time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"Failed to fetch {url}")


def fetch_aon(category: str, source_fields: list[str], size: int) -> list[dict]:
    query = {
        "size": size,
        "query": {
            "bool": {
                "must": [
                    {"term": {"category": category}},
                    {"terms": {"primary_source.keyword": AON_REMASTER_SOURCES}},
                    {"term": {"rarity": "common"}},
                ]
            }
        },
        "_source": source_fields,
    }
    result = fetch_json(AON_SEARCH_URL, data=json.dumps(query).encode("utf-8"))
    total = result["hits"]["total"]["value"]
    hits = [hit["_source"] for hit in result["hits"]["hits"]]
    if total > len(hits):
        raise RuntimeError(
            f"AoN {category} query returned {len(hits)} of {total} hits - raise size above {size}.")
    return hits


# ------------------------------------------------------------------- mapping

def slugify(name: str) -> str:
    slug = name.lower().replace("-", "_")
    slug = re.sub(r"[^a-z0-9_]+", "_", slug).strip("_")
    return re.sub(r"_+", "_", slug)


def dedupe_slug(slug: str, display: str, generated: dict) -> str:
    base, candidate, n = slug, slug, 2
    while candidate in generated and generated[candidate].get("display") != display:
        candidate = f"{base}_{n}"
        n += 1
    return candidate


def spell_classes(spell: dict) -> list[str]:
    classes: set[str] = set()
    for tradition in spell.get("tradition") or []:
        classes.update(TRADITION_TO_CLASSES.get(str(tradition).lower(), []))
    for trait in spell.get("trait") or []:
        if str(trait).lower() in CLASS_TRAITS:
            classes.add(str(trait).lower())
    return sorted(classes)


def spell_rank(spell: dict) -> int:
    traits = {str(t).lower() for t in spell.get("trait") or []}
    if "cantrip" in traits:
        return 0
    return int(spell.get("level") or 1)


def parse_save(raw: object) -> dict | None:
    if not raw or not isinstance(raw, str):
        return None
    match = SAVE_TYPE_RE.search(raw)
    if not match:
        return {"raw": raw}
    return {"raw": raw, "type": match.group(0).lower(), "basic": raw.lower().startswith("basic")}


def map_spell(spell: dict) -> dict:
    # Reference data for now: AoN's structured fields carry no damage/effect formulas
    # (those live in prose), so PF2e spells ship without TRCE - a later slice that can
    # express saves, areas, and heightened ranks makes them executable.
    meta: dict = {"kind": "spell", "rank": spell_rank(spell)}
    traditions = sorted({str(t).lower() for t in spell.get("tradition") or []})
    if traditions:
        meta["traditions"] = traditions
    traits = sorted({str(t).lower() for t in spell.get("trait") or []})
    if traits:
        meta["traits"] = traits
    classes = spell_classes(spell)
    if classes:
        meta["classes"] = classes
    if spell.get("actions"):
        meta["actions"] = spell["actions"]
    if isinstance(spell.get("actions_number"), int):
        meta["actionsNumber"] = spell["actions_number"]
    if spell.get("component"):
        meta["components"] = sorted({str(c).lower() for c in spell["component"]})
    if spell.get("range") is not None:
        meta["range"] = spell["range"]
    if spell.get("range_raw"):
        meta["rangeRaw"] = spell["range_raw"]
    area = spell.get("area") or []
    area_types = spell.get("area_type") or []
    if area or area_types:
        meta["area"] = {"values": list(area), "types": [str(t).lower() for t in area_types]}
    save = parse_save(spell.get("saving_throw"))
    if save:
        meta["savingThrow"] = save
    if spell.get("school"):
        meta["school"] = str(spell["school"]).lower()
    if spell.get("cost"):
        meta["cost"] = spell["cost"]
    if spell.get("heighten"):
        meta["heighten"] = [str(h) for h in spell["heighten"]]
    if spell.get("heighten_level"):
        meta["heightenLevels"] = list(spell["heighten_level"])
    if spell.get("primary_source"):
        meta["sourceBook"] = spell["primary_source"]
    return {"id": "", "name": spell["name"], "metadata": meta}


def feat_classes(feat: dict) -> list[str]:
    traits = {str(t).lower() for t in feat.get("trait") or []}
    return sorted(traits & KNOWN_CLASSES)


def section_text(pattern: re.Pattern, text: str) -> str | None:
    match = pattern.search(text or "")
    if not match:
        return None
    return match.group(1).strip().rstrip(".") or None


def map_feat(feat: dict) -> dict:
    text = feat.get("text") or ""
    meta: dict = {"kind": "feat"}
    if feat.get("level") is not None:
        meta["level"] = feat["level"]
    traits = sorted({str(t).lower() for t in feat.get("trait") or []})
    if traits:
        meta["traits"] = traits
    classes = feat_classes(feat)
    if classes:
        meta["classes"] = classes
    if feat.get("type"):
        meta["featType"] = feat["type"]
    if feat.get("actions"):
        meta["actions"] = feat["actions"]
    prereq = section_text(PREREQ_RE, text)
    if prereq:
        meta["prerequisites"] = prereq
    frequency = section_text(FREQUENCY_RE, text)
    if frequency:
        meta["frequency"] = frequency
    trigger = section_text(TRIGGER_RE, text)
    if trigger:
        meta["trigger"] = trigger
    cost = section_text(COST_RE, text)
    if cost:
        meta["cost"] = cost
    if feat.get("primary_source"):
        meta["sourceBook"] = feat["primary_source"]
    return {"id": "", "name": feat["name"], "metadata": meta}


# ------------------------------------------- classes/ancestries/gear/patrons

def as_str_list(value: object) -> list:
    if value is None:
        return []
    if isinstance(value, list):
        return [str(v) for v in value]
    return [str(value)]


def map_pf2e_class(doc: dict) -> dict:
    props: dict = {}
    if doc.get("hp") is not None:
        props["hitPoints"] = doc["hp"]
    for key, name in (("attack_proficiency", "attackProficiency"),
                      ("defense_proficiency", "defenseProficiency"),
                      ("fortitude_proficiency", "fortitudeProficiency"),
                      ("reflex_proficiency", "reflexProficiency"),
                      ("will_proficiency", "willProficiency"),
                      ("perception_proficiency", "perceptionProficiency"),
                      ("skill_proficiency", "skillProficiency")):
        if doc.get(key):
            props[name] = doc[key]
    attributes = as_str_list(doc.get("attribute"))
    if attributes:
        props["keyAttributes"] = attributes
    if isinstance(doc.get("speed"), (int, float)):
        props["speed"] = doc["speed"]
    traits = sorted({str(t).lower() for t in doc.get("trait") or []})
    entry: dict = {"id": "", "name": doc["name"], "properties": props,
                   "metadata": {"term": "class"}}
    if traits:
        entry["tags"] = traits
    if doc.get("primary_source"):
        entry["metadata"]["sourceBook"] = doc["primary_source"]
    return entry


def map_pf2e_ancestry(doc: dict) -> dict:
    props: dict = {}
    if doc.get("hp") is not None:
        props["hitPoints"] = doc["hp"]
    size = doc.get("size")
    if isinstance(size, list) and len(size) == 1:
        size = size[0]
    if size:
        props["size"] = size
    if isinstance(doc.get("speed"), (int, float)):
        props["speed"] = doc["speed"]
    boosts = as_str_list(doc.get("attribute"))
    if boosts:
        props["abilityBoosts"] = boosts
    flaw = as_str_list(doc.get("attribute_flaw"))
    if flaw:
        props["abilityFlaw"] = flaw
    languages = as_str_list(doc.get("language"))
    if languages:
        props["languages"] = languages
    if doc.get("vision"):
        props["vision"] = as_str_list(doc["vision"])
    traits = sorted({str(t).lower() for t in doc.get("trait") or []})
    entry: dict = {"id": "", "name": doc["name"], "properties": props,
                   "metadata": {"term": "ancestry"}}
    if traits:
        entry["tags"] = traits
    if doc.get("primary_source"):
        entry["metadata"]["sourceBook"] = doc["primary_source"]
    return entry


def map_pf2e_heritage(doc: dict, ancestry_names: list[str]) -> dict:
    # Heritage docs carry no ancestry link - match "<Name> <Ancestry>" suffixes
    # ("Ancient-Blooded Dwarf"); anything unmatched is versatile (any ancestry).
    props: dict = {}
    lowered = str(doc.get("name", "")).lower()
    for ancestry in ancestry_names:
        if lowered == ancestry or lowered.endswith(" " + ancestry):
            props["ancestryOf"] = ancestry
            break
    entry: dict = {"id": "", "name": doc["name"], "properties": props,
                   "metadata": {"term": "heritage"}}
    if doc.get("primary_source"):
        entry["metadata"]["sourceBook"] = doc["primary_source"]
    return entry


def map_pf2e_background(doc: dict) -> dict:
    props: dict = {}
    boosts = as_str_list(doc.get("attribute"))
    if boosts:
        props["abilityBoosts"] = boosts
    if doc.get("skill"):
        props["skill"] = doc["skill"]
    if doc.get("feat"):
        props["feat"] = doc["feat"]
    entry: dict = {"id": "", "name": doc["name"], "properties": props,
                   "metadata": {"term": "background"}}
    if doc.get("primary_source"):
        entry["metadata"]["sourceBook"] = doc["primary_source"]
    return entry


def map_pf2e_patron(doc: dict) -> dict:
    # Witch patrons: structured tradition/skill/hex cantrip/lesson spells.
    props: dict = {}
    if doc.get("tradition"):
        props["spellList"] = as_str_list(doc["tradition"])
    if doc.get("skill"):
        props["patronSkill"] = as_str_list(doc["skill"])
    if doc.get("hex_cantrip"):
        props["hexCantrip"] = as_str_list(doc["hex_cantrip"])
    if doc.get("spell"):
        props["lessonSpells"] = as_str_list(doc["spell"])
    entry: dict = {"id": "", "name": doc["name"], "properties": props,
                   "metadata": {"term": "patron"}}
    if doc.get("primary_source"):
        entry["metadata"]["sourceBook"] = doc["primary_source"]
    return entry


def map_pf2e_item(doc: dict, category: str) -> dict:
    props: dict = {"category": category}
    if doc.get("level") is not None:
        props["level"] = doc["level"]
    if doc.get("price"):
        props["price"] = doc["price"]
    if doc.get("bulk") is not None:
        props["bulk"] = doc["bulk"]
    if doc.get("damage"):
        props["damage"] = doc["damage"]
    if doc.get("damage_die"):
        props["damageDie"] = doc["damage_die"]
    if doc.get("damage_type"):
        props["damageType"] = as_str_list(doc["damage_type"])
    if doc.get("hands") is not None:
        props["hands"] = doc["hands"]
    if doc.get("ac") is not None:
        props["armorClass"] = doc["ac"]
    if doc.get("armor_category"):
        props["armorCategory"] = doc["armor_category"]
    if doc.get("weapon_category"):
        props["weaponCategory"] = doc["weapon_category"]
    if doc.get("weapon_group"):
        props["weaponGroup"] = as_str_list(doc["weapon_group"])
    if doc.get("weapon_type"):
        props["weaponType"] = doc["weapon_type"]
    if doc.get("item_category"):
        props["itemCategory"] = doc["item_category"]
    traits = sorted({str(t).lower() for t in doc.get("trait") or []})
    entry: dict = {"id": "", "name": doc["name"], "properties": props,
                   "metadata": {"term": "equipment"}}
    if traits:
        entry["tags"] = traits
    if doc.get("primary_source"):
        entry["metadata"]["sourceBook"] = doc["primary_source"]
    return entry


# ------------------------------------------------------------------- pack io

ATTRIBUTION_TEMPLATE = """# Attribution for packs/{pack_id}

Mechanics data (names, levels, traits, actions, areas, saves, prerequisites)
sourced from Pathfinder 2e Remastered - Player Core and Player Core 2 - by
Paizo Inc., via the official Archives of Nethys database
(https://elasticsearch.aonprd.com), released under the Open RPG Creative
License (ORC). Only common-rarity entries from those two books are imported;
legacy (pre-Remaster) content is excluded by the import query itself.

No rulebook prose is reproduced: feat/spell text and summaries are read only to
extract prerequisite/frequency/trigger names and are never copied into the YAML
(see scripts/import_pf2e.py).

Generated {generated_at} UTC: {spell_count} spells + {feat_count} feats +
{class_count} classes + {ancestry_count} ancestries + {heritage_count} heritages +
{background_count} backgrounds + {patron_count} patrons + {gear_count} equipment.
Regenerate with: python3 scripts/import_pf2e.py
"""


def write_pack(pack_dir: Path, datasets: dict[str, dict[str, dict]]) -> None:
    if pack_dir.exists():
        shutil.rmtree(pack_dir)
    subfolders = {"spells": "spells", "feats": "feats", "classes": "classes",
                  "ancestries": "ancestries", "heritages": "heritages",
                  "backgrounds": "backgrounds", "patrons": "patrons", "equipment": "equipment"}
    for sub in subfolders.values():
        (pack_dir / sub).mkdir(parents=True)

    generated_at = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M")
    counts = {name: len(entries) for name, entries in datasets.items()}
    manifest = {
        "id": PACK_ID,
        "name": PACK_NAME,
        "version": "1.0.0",
        "systemId": SYSTEM_ID,
        "minAppVersion": "1.0.0",
        "priority": 10,
        "source": "PF2e Remastered (Player Core / Player Core 2) via Archives of Nethys (ORC)",
        "generatedAtUtc": generated_at,
        "counts": counts,
    }
    (pack_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (pack_dir / "ATTRIBUTION.md").write_text(
        ATTRIBUTION_TEMPLATE.format(
            pack_id=PACK_ID, generated_at=generated_at,
            spell_count=len(datasets["spells"]), feat_count=len(datasets["feats"]),
            class_count=len(datasets["classes"]), ancestry_count=len(datasets["ancestries"]),
            heritage_count=len(datasets["heritages"]), background_count=len(datasets["backgrounds"]),
            patron_count=len(datasets["patrons"]), gear_count=len(datasets["equipment"])),
        encoding="utf-8")

    for name, entries in datasets.items():
        for slug in sorted(entries):
            write_entry(pack_dir / subfolders[name] / f"{slug}.yaml", entries[slug])


def validate_mapping(datasets: dict[str, dict[str, dict]]) -> None:
    problems: list[str] = []
    for slug, spell in datasets["spells"].items():
        if not spell.get("id") or not spell.get("name"):
            problems.append(f"spell {slug}: missing id/name")
        rank = (spell.get("metadata") or {}).get("rank")
        if not isinstance(rank, int) or not 0 <= rank <= 10:
            problems.append(f"spell {slug}: bad rank {rank!r}")
    for slug, feat in datasets["feats"].items():
        if not feat.get("id") or not feat.get("name"):
            problems.append(f"feat {slug}: missing id/name")
        level = (feat.get("metadata") or {}).get("level")
        if level is not None and (not isinstance(level, int) or not 1 <= level <= 20):
            problems.append(f"feat {slug}: bad level {level!r}")
    for name in ("classes", "ancestries", "heritages", "backgrounds", "patrons", "equipment"):
        for slug, entry in datasets[name].items():
            if not entry.get("id") or not entry.get("name"):
                problems.append(f"{name}/{slug}: missing id/name")
    if problems:
        raise RuntimeError(f"{len(problems)} mapping problems:\n" + "\n".join(problems[:20]))


SPELL_FIELDS = ["name", "level", "trait", "tradition", "actions", "actions_number",
                "area", "area_type", "range", "range_raw", "saving_throw", "school",
                "spell_type", "component", "cost", "heighten", "heighten_level",
                "primary_source", "rarity"]
FEAT_FIELDS = ["name", "level", "trait", "type", "actions", "actions_number",
               "text", "primary_source", "rarity"]
CLASS_FIELDS = ["name", "trait", "hp", "attribute", "attack_proficiency", "defense_proficiency",
                "fortitude_proficiency", "reflex_proficiency", "will_proficiency",
                "perception_proficiency", "skill_proficiency", "speed", "primary_source", "rarity"]
ANCESTRY_FIELDS = ["name", "trait", "hp", "size", "speed", "attribute", "attribute_flaw",
                   "language", "vision", "primary_source", "rarity"]
HERITAGE_FIELDS = ["name", "primary_source", "rarity"]
BACKGROUND_FIELDS = ["name", "attribute", "skill", "feat", "primary_source", "rarity"]
PATRON_FIELDS = ["name", "tradition", "skill", "hex_cantrip", "spell", "primary_source", "rarity"]
ITEM_FIELDS = ["name", "level", "trait", "bulk", "price", "damage", "damage_die", "damage_type",
               "hands", "ac", "armor_category", "weapon_category", "weapon_group", "weapon_type",
               "item_category", "primary_source", "rarity"]


def collect(raw: list[dict], mapper, label: str) -> dict[str, dict]:
    # Same item reprinted in both books (some Adventuring-gear entries exist in Player
    # Core AND Player Core 2 as separate docs) collapses to one file. Sort first so
    # the survivor is deterministic (Player Core sorts before Player Core 2) rather
    # than whatever order elasticsearch scored the duplicates in.
    generated: dict[str, dict] = {}
    dupes = 0
    for doc in sorted(raw, key=lambda d: ((d.get("name") or ""), (d.get("primary_source") or ""))):
        name = (doc.get("name") or "").strip()
        if not name:
            continue
        slug = slugify(name)
        if not slug:
            continue
        if slug in generated and generated[slug].get("display") == name:
            dupes += 1
            continue
        slug = dedupe_slug(slug, name, generated)
        entry = mapper(doc)
        entry["id"] = slug
        entry["display"] = name  # not emitted; dedupe anchor only
        generated[slug] = entry
    for entry in generated.values():
        entry.pop("display", None)
    if dupes:
        print(f"  {label}: {dupes} same-name reprint dupes collapsed")
    return generated


def main() -> int:
    parser = argparse.ArgumentParser(description="Import PF2e Remastered rules into packs/pf2e-remastered.")
    parser.add_argument("--output", default=str(ROOT / "packs" / PACK_ID), help="Pack directory to write.")
    parser.add_argument("--no-install", action="store_true", help="Skip mirroring the pack into plugins/ (dev runtime dir).")
    parser.add_argument("--limit", type=int, default=0, help="Import at most N entries per dataset (smoke test).")
    args = parser.parse_args()

    print(f"Fetching PF2e spells from {AON_SEARCH_URL} ...")
    raw_spells = fetch_aon("spell", SPELL_FIELDS, size=1000)
    print("Fetching PF2e feats ...")
    raw_feats = fetch_aon("feat", FEAT_FIELDS, size=2000)
    print("Fetching PF2e classes ...")
    raw_classes = fetch_aon("class", CLASS_FIELDS, size=100)
    print("Fetching PF2e ancestries ...")
    raw_ancestries = fetch_aon("ancestry", ANCESTRY_FIELDS, size=100)
    print("Fetching PF2e heritages ...")
    raw_heritages = fetch_aon("heritage", HERITAGE_FIELDS, size=500)
    print("Fetching PF2e backgrounds ...")
    raw_backgrounds = fetch_aon("background", BACKGROUND_FIELDS, size=200)
    print("Fetching PF2e patrons ...")
    raw_patrons = fetch_aon("patron", PATRON_FIELDS, size=100)
    print("Fetching PF2e weapons/armor/equipment/shields ...")
    raw_weapons = fetch_aon("weapon", ITEM_FIELDS, size=500)
    raw_armor = fetch_aon("armor", ITEM_FIELDS, size=100)
    raw_equipment = fetch_aon("equipment", ITEM_FIELDS, size=1000)
    raw_shields = fetch_aon("shield", ITEM_FIELDS, size=100)

    if args.limit:
        raw_spells = raw_spells[:args.limit]
        raw_feats = raw_feats[:args.limit]
        raw_classes = raw_classes[:args.limit]
        raw_ancestries = raw_ancestries[:args.limit]
        raw_heritages = raw_heritages[:args.limit]
        raw_backgrounds = raw_backgrounds[:args.limit]
        raw_patrons = raw_patrons[:args.limit]
        raw_weapons = raw_weapons[:args.limit]
        raw_armor = raw_armor[:args.limit]
        raw_equipment = raw_equipment[:args.limit]
        raw_shields = raw_shields[:args.limit]

    print("Mapping to Codex YAML...")
    datasets = {
        "spells": collect(raw_spells, map_spell, "spells"),
        "feats": collect(raw_feats, map_feat, "feats"),
        "classes": collect(raw_classes, map_pf2e_class, "classes"),
        "ancestries": collect(raw_ancestries, map_pf2e_ancestry, "ancestries"),
        "backgrounds": collect(raw_backgrounds, map_pf2e_background, "backgrounds"),
        "patrons": collect(raw_patrons, map_pf2e_patron, "patrons"),
    }
    ancestry_names = [d.get("name", "").lower() for d in raw_ancestries]
    datasets["heritages"] = collect(
        raw_heritages, lambda doc: map_pf2e_heritage(doc, ancestry_names), "heritages")

    # Rules entries share one System:Id namespace per system, but PF2e reuses names
    # across types (Aiuvarin/Dromaar are both ancestries and versatile heritages).
    # Suffix the heritage/background/patron side so both entries survive loading.
    taken = set(datasets["classes"]) | set(datasets["ancestries"])
    for name in ("heritages", "backgrounds", "patrons"):
        renamed: dict[str, dict] = {}
        for slug, entry in datasets[name].items():
            if slug in taken:
                slug = f"{slug}_{name.rstrip('s')}"
                entry["id"] = slug
            taken.add(slug)
            renamed[slug] = entry
        datasets[name] = renamed
    datasets["equipment"] = collect(raw_weapons, lambda doc: map_pf2e_item(doc, "weapon"), "weapons")
    for extra in (collect(raw_armor, lambda doc: map_pf2e_item(doc, "armor"), "armor"),
                  collect(raw_equipment, lambda doc: map_pf2e_item(doc, "equipment"), "equipment"),
                  collect(raw_shields, lambda doc: map_pf2e_item(doc, "shield"), "shields")):
        for slug, entry in extra.items():
            # Same slug in two categories must not silently drop one file - prefix the
            # category (armor_buckler vs weapon_buckler) instead of overwriting.
            merged = datasets["equipment"]
            if slug in merged and merged[slug]["name"] != entry["name"]:
                slug = dedupe_slug(
                    f"{entry['properties'].get('category', 'item')}_{slug}", entry["name"], merged)
                entry["id"] = slug
            merged[slug] = entry
    validate_mapping(datasets)

    pack_dir = Path(args.output)
    write_pack(pack_dir, datasets)
    print(f"Wrote {len(datasets['spells'])} spells + {len(datasets['feats'])} feats + "
          f"{len(datasets['classes'])} classes + {len(datasets['ancestries'])} ancestries + "
          f"{len(datasets['heritages'])} heritages + {len(datasets['backgrounds'])} backgrounds + "
          f"{len(datasets['patrons'])} patrons + {len(datasets['equipment'])} equipment to {pack_dir}")

    if not args.no_install:
        dest = ROOT / "plugins" / PACK_ID
        if dest.exists():
            shutil.rmtree(dest)
        shutil.copytree(pack_dir, dest)
        print(f"Mirrored to {dest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
