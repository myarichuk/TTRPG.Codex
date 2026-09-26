#!/usr/bin/env python3
"""Import SRD 5.1 (2014 rules) spells, monsters, classes, races, and equipment from
dnd5eapi into a Codex content pack.

Mechanics-only policy: names, numbers, enums, and short factual labels (material
components, prerequisites) are emitted. Descriptive prose fields (``desc``,
``higher_level``, monster action text, class feature text) are NEVER copied - the
generated YAML carries no copyrightable expression, only game facts, with full
source attribution in the pack manifest header and ATTRIBUTION.md.

Output: packs/srd51-full/{manifest.json,ATTRIBUTION.md,spells/,actors/,classes/,
ancestries/,equipment/*.yaml}, mirrored to plugins/srd51-full/ (the directory the
dev-time PluginLoader reads) unless --no-install is given. One file per entry, so
diffs stay reviewable.

Requires network access to https://www.dnd5eapi.co (SRD 5.1, CC-BY-4.0).
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
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACK_ID = "srd51-full"
PACK_NAME = "SRD 5.1 Full (5e)"
SYSTEM_ID = "DnD5e"
API_BASE = "https://www.dnd5eapi.co"

FILE_HEADER = (
    "# Source: SRD 5.1 by Wizards of the Coast LLC (via https://www.dnd5eapi.co),\n"
    "# CC-BY-4.0. Mechanics only - no SRD prose is reproduced (see ../ATTRIBUTION.md).\n"
)

GP_COST_RE = re.compile(r"([\d,]+)\s*gp", re.IGNORECASE)
FEET_RE = re.compile(r"(\d+)\s*ft", re.IGNORECASE)
MOD_RE = re.compile(r"\s*[+-]\s*MOD\b", re.IGNORECASE)


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
    # Bare scalars stay bare; anything load-bearing gets JSON double quotes (valid YAML).
    # Strings that would parse back as another type ("2", "true", "null") always quote.
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
    # One-item list: matches existing pack style and the loader's List<T> fast path.
    lines.extend(emit_yaml([entry]))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


# ------------------------------------------------------------------- fetching

def fetch_json(url: str, retries: int = 4) -> dict:
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "TTRPG.Codex-importer/1.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception:
            if attempt == retries - 1:
                raise
            time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"Failed to fetch {url}")


def fetch_json_any(url: str, retries: int = 4):
    """fetch_json for endpoints that return a bare list (class/level collections)."""
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "TTRPG.Codex-importer/1.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception:
            if attempt == retries - 1:
                raise
            time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"Failed to fetch {url}")


def fetch_all_details(index_url: str, label: str) -> dict[str, dict]:
    index = fetch_json(f"{API_BASE}{index_url}")
    entries = index["results"]
    details: dict[str, dict] = {}
    errors: list[str] = []

    def load(entry: dict) -> tuple[str, dict]:
        detail = fetch_json(f"{API_BASE}{entry['url']}")
        return detail["index"], detail

    with ThreadPoolExecutor(max_workers=12) as pool:
        futures = {pool.submit(load, e): e for e in entries}
        for i, future in enumerate(as_completed(futures), 1):
            try:
                slug, detail = future.result()
                details[slug] = detail
            except Exception as exc:  # noqa: BLE001 - collected, reported, fatal below
                errors.append(f"{futures[future].get('index', '?')}: {exc}")
            if i % 50 == 0:
                print(f"  {label}: {i}/{len(entries)}")

    if errors:
        raise RuntimeError(f"{len(errors)} {label} detail fetches failed:\n" + "\n".join(errors[:10]))
    return details


# ------------------------------------------------------------------- mapping

def parse_feet(text: object) -> int | str | None:
    if isinstance(text, (int, float)):
        return int(text)
    if not isinstance(text, str):
        return None
    match = FEET_RE.search(text)
    return int(match.group(1)) if match else text


def strip_mod(dice: str) -> str:
    """'1d8 + MOD' -> '1d8': the engine grammar has no MOD; the full formula stays in metadata."""
    return MOD_RE.sub("", dice).strip()


def map_save(dc: dict | None) -> dict | None:
    if not dc:
        return None
    out: dict = {}
    dc_type = dc.get("dc_type") or {}
    if dc_type.get("index"):
        out["ability"] = dc_type["index"]
    if dc.get("dc_success"):
        out["success"] = dc["dc_success"]
    if dc.get("dc_value") is not None:
        out["value"] = dc["dc_value"]
    if dc.get("success_type"):
        out["success"] = dc["success_type"]
    return out or None


def base_dice(by_level: dict) -> tuple[str, dict] | None:
    """Lowest-level entry is the base formula; the whole table goes to metadata."""
    if not by_level:
        return None
    ordered = sorted(by_level.items(), key=lambda kv: int(kv[0]))
    return ordered[0][1], {str(k): v for k, v in ordered}


def dice_or_amount(formula: str) -> dict:
    """'8d6' -> {"dice": ...}; flat '5' (aid) -> {"amount": 5} - the engine dice
    grammar has no bare numbers, so those must ride the amount path instead."""
    if "d" in formula.lower():
        return {"dice": formula}
    return {"amount": int(formula.strip())}


def map_spell(detail: dict) -> dict:
    spell_id = detail["index"]
    level = int(detail.get("level", 0))
    entry: dict = {"id": spell_id, "name": detail["name"]}
    entry["triggers"] = [{"type": "Cast"}]

    requires = []
    if level >= 1:
        pool = f"Slots{level}"
        requires.append({"type": "ResourceRequirement", "params": {"pool": pool, "minAmount": 1}})
        entry["costs"] = [{"type": "ResourceCost", "params": {"pool": pool, "amount": 1}}]
    if detail.get("attack_type"):
        # Bare: bonus and target AC resolve at runtime (caster sheet + target sheet).
        requires.append({"type": "Attack"})
    if requires:
        entry["requires"] = requires

    # Executable effects: one Damage effect per damage source (ice storm's bludgeoning
    # AND cold both execute), or a Heal effect for pure healing. Mixed damage+heal
    # (vampiric touch heals half of damage dealt) keeps damage executable and healing
    # in metadata - the engine cannot express "half of dealt" yet.
    damage_entries = detail.get("damage") or []
    heal_table = detail.get("heal_at_slot_level") or {}
    save = map_save(detail.get("dc"))
    effects = []
    for dmg in damage_entries:
        table = dmg.get("damage_at_slot_level") or dmg.get("damage_at_character_level") or {}
        based = base_dice(table)
        if not based:
            continue
        params = {"pool": "HP", "damageType": (dmg.get("damage_type") or {}).get("index", "")}
        params.update(dice_or_amount(strip_mod(based[0])))
        if save:
            # No saveDc: DCs are caster-derived (8 + proficiency + modifier) and arrive
            # at runtime via the execution context. See docs/RULES_CONTENT.md.
            params["saveAbility"] = save.get("ability", "")
            params["saveSuccess"] = save.get("success", "negates")
        effects.append({"type": "Damage", "params": params})
    if heal_table and not damage_entries:
        based = base_dice(heal_table)
        if based:
            params = {"pool": "HP"}
            params.update(dice_or_amount(strip_mod(based[0])))
            effects.append({"type": "Heal", "params": params})
    if effects:
        entry["effects"] = effects

    meta: dict = {"kind": "spell", "level": level}
    if detail.get("school", {}).get("index"):
        meta["school"] = detail["school"]["index"]
    meta["classes"] = sorted({c["index"] for c in detail.get("classes", [])})
    if detail.get("casting_time"):
        meta["castingTime"] = detail["casting_time"]
    if detail.get("range"):
        meta["range"] = detail["range"]
    if detail.get("area_of_effect"):
        meta["area"] = detail["area_of_effect"]
    if detail.get("components"):
        meta["components"] = detail["components"]
    if detail.get("material"):
        meta["material"] = detail["material"]
        match = GP_COST_RE.search(detail["material"])
        if match:
            meta["materialCost"] = float(match.group(1).replace(",", ""))
            meta["materialConsumed"] = "consum" in detail["material"].lower()
    meta["ritual"] = bool(detail.get("ritual"))
    meta["concentration"] = bool(detail.get("concentration"))
    if detail.get("duration"):
        meta["duration"] = detail["duration"]
    if detail.get("attack_type"):
        meta["attackType"] = detail["attack_type"]
    save = map_save(detail.get("dc"))
    if save:
        meta["save"] = save
    for dmg in damage_entries:
        if dmg.get("damage_at_slot_level"):
            meta.setdefault("damageAtSlotLevel", {})[(dmg.get("damage_type") or {}).get("index", "?")] = dmg["damage_at_slot_level"]
        if dmg.get("damage_at_character_level"):
            meta["damageAtCharacterLevel"] = dmg["damage_at_character_level"]
    if heal_table:
        meta["healAtSlotLevel"] = heal_table
    entry["metadata"] = meta
    return entry


def strip_prefix(index: str, prefix: str) -> str:
    return index[len(prefix):] if index.startswith(prefix) else index


def map_proficiencies(proficiencies: list) -> tuple[dict, dict, list]:
    saves: dict = {}
    skills: dict = {}
    other: list = []
    for prof in proficiencies or []:
        ref = prof.get("proficiency") or {}
        index = ref.get("index", "")
        value = prof.get("value")
        if index.startswith("saving-throw-"):
            saves[strip_prefix(index, "saving-throw-")] = value
        elif index.startswith("skill-"):
            skills[strip_prefix(index, "skill-")] = value
        else:
            other.append({"name": ref.get("name", index), "value": value})
    return saves, skills, other


def map_damage_list(damage: list) -> list:
    out = []
    for entry in damage or []:
        item: dict = {}
        dtype = (entry.get("damage_type") or {}).get("index")
        if dtype:
            item["type"] = dtype
        if entry.get("damage_dice"):
            item["dice"] = entry["damage_dice"]
        if item:
            out.append(item)
    return out


def map_action_list(actions: list) -> list:
    out = []
    for action in actions or []:
        item: dict = {"name": action.get("name", "?")}
        for key in ("attack_bonus", "reach", "range", "targets"):
            if action.get(key) is not None:
                item["".join(w.capitalize() if i else w for i, w in enumerate(key.split("_")))] = action[key]
        damage = map_damage_list(action.get("damage") or [])
        if damage:
            item["damage"] = damage
        save = map_save(action.get("dc"))
        if save:
            item["save"] = save
        if action.get("usage"):
            item["usage"] = action["usage"]
        options = action.get("actions") or []
        if options:
            item["options"] = [{"action": o.get("action_name", "?"), "count": o.get("count", 1)} for o in options]
        out.append(item)
    return out


def map_monster(detail: dict) -> dict:
    monster_id = detail["index"]
    entry: dict = {"id": monster_id, "name": detail["name"]}
    entry["tags"] = [str(detail.get("type", "?")).lower(), str(detail.get("size", "?")).lower()]
    entry["resources"] = {"HP": int(detail["hit_points"])}

    props: dict = {}
    armor = detail.get("armor_class") or []
    if armor:
        props["armorClass"] = max(a.get("value", 0) for a in armor)
        props["armorTypes"] = sorted({a.get("type", "?") for a in armor})
    if detail.get("hit_dice"):
        props["hitDice"] = detail["hit_dice"]
    if detail.get("hit_points_roll"):
        props["hitPointsRoll"] = detail["hit_points_roll"]
    speed = {k: parse_feet(v) for k, v in (detail.get("speed") or {}).items()}
    if speed:
        props["speed"] = speed
    for score in ("strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma"):
        if detail.get(score) is not None:
            props[score] = detail[score]
    saves, skills, other_profs = map_proficiencies(detail.get("proficiencies") or [])
    if saves:
        props["savingThrows"] = saves
    if skills:
        props["skills"] = skills
    if other_profs:
        props["otherProficiencies"] = other_profs
    for key in ("damage_vulnerabilities", "damage_resistances", "damage_immunities", "condition_immunities"):
        values = detail.get(key) or []
        if values:
            camel = "".join(w.capitalize() if i else w for i, w in enumerate(key.split("_")))
            props[camel] = values
    senses = {k: parse_feet(v) for k, v in (detail.get("senses") or {}).items()}
    if senses:
        props["senses"] = senses
    if detail.get("languages"):
        props["languages"] = [lang.strip() for lang in detail["languages"].split(",")]
    if detail.get("challenge_rating") is not None:
        props["challengeRating"] = detail["challenge_rating"]
    if detail.get("xp") is not None:
        props["xp"] = detail["xp"]
    if detail.get("proficiency_bonus") is not None:
        props["proficiencyBonus"] = detail["proficiency_bonus"]
    for key in ("size", "type", "subtype", "alignment"):
        if detail.get(key):
            props[key] = detail[key]
    specials = []
    for spec in detail.get("special_abilities") or []:
        item: dict = {"name": spec.get("name", "?")}
        damage = map_damage_list(spec.get("damage") or [])
        if damage:
            item["damage"] = damage
        save = map_save(spec.get("dc"))
        if save:
            item["save"] = save
        specials.append(item)
    if specials:
        props["specialAbilities"] = specials
    actions = map_action_list(detail.get("actions") or [])
    if actions:
        props["actions"] = actions
    legendary = map_action_list(detail.get("legendary_actions") or [])
    if legendary:
        props["legendaryActions"] = legendary
    reactions = map_action_list(detail.get("reactions") or [])
    if reactions:
        props["reactions"] = reactions
    entry["properties"] = props
    entry["metadata"] = {"kind": "monster"}
    return entry


# ------------------------------------------------- classes/subclasses/races/gear

def map_levels(levels: list) -> list:
    """Class/subclass levels: feature NAMES by level plus numeric progressions. The
    /levels collection embeds every level inline, so one fetch per class suffices."""
    out = []
    for level in levels or []:
        item: dict = {"level": level.get("level")}
        if level.get("prof_bonus") is not None:
            item["profBonus"] = level["prof_bonus"]
        if level.get("ability_score_bonuses") is not None:
            item["abilityScoreBonuses"] = level["ability_score_bonuses"]
        features = [f.get("name", "?") for f in level.get("features", [])]
        if features:
            item["features"] = features
        if level.get("spellcasting"):
            item["spellcasting"] = level["spellcasting"]
        if level.get("class_specific"):
            item["classSpecific"] = level["class_specific"]
        out.append(item)
    return out


def map_option_source(src: dict) -> dict:
    """One equipment option set: a category grant, an option list, or a fixed item."""
    if src.get("option_set_type") == "equipment_category":
        return {"category": (src.get("equipment_category") or {}).get("name", "?")}
    options = src.get("options", [])
    if options:
        return {"options": [map_choice_option(o) for o in options]}
    if "equipment" in src:
        return {"name": (src.get("equipment") or {}).get("name", "?"),
                "quantity": src.get("quantity", 1)}
    return {"name": src.get("name", "?")}


def map_choice_option(option: dict) -> dict:
    """Starting-equipment options nest (choice of choice) - map recursively, names only."""
    opt_type = option.get("option_type", "")
    if opt_type == "counted_reference":
        ref = option.get("of", {})
        return {"name": ref.get("name", "?"), "quantity": option.get("count", 1)}
    if opt_type == "choice":
        nested = option.get("choice", {})
        mapped: dict = {"choose": nested.get("choose", 1)}
        mapped.update(map_option_source(nested.get("from", {})))
        return mapped
    if opt_type == "multiple":
        # A bundle granted atomically ("a light crossbow and 20 bolts") - one choice,
        # several items. Never fall through to {"name": "?"}: the wizard renders every
        # option, and a "?" entry is a silent data hole.
        return {"bundle": [map_choice_option(item) for item in option.get("items", [])]}
    mapped = map_option_source(option.get("from", option))
    if mapped.get("name") == "?":
        raise ValueError(f"Unmapped equipment option shape: {sorted(option.keys())}")
    return mapped


def proficiency_option_names(src: dict) -> list:
    """Flatten proficiency-choice options to display names, recursing through
    nested choices (monk: 'one artisan tool type OR one instrument'), since the
    wizard renders proficiencyChoices as a flat name list."""
    out: list = []
    for o in src.get("options", []) or []:
        if not isinstance(o, dict):
            out.append(str(o))
            continue
        if o.get("option_type") == "choice":
            nested_from = o.get("choice", {}).get("from", {})
            if nested_from.get("option_set_type") == "equipment_category":
                out.append("Any " + (nested_from.get("equipment_category") or {}).get("name", "?"))
            else:
                out.extend(proficiency_option_names(nested_from))
        else:
            item = o.get("item") or {}
            out.append(item.get("name", "?") if isinstance(item, dict) else str(o))
    return out


def map_class(detail: dict, levels: list, spell_list: list) -> dict:
    entry: dict = {"id": detail["index"], "name": detail["name"]}
    props: dict = {"hitDie": detail.get("hit_die")}
    props["savingThrows"] = sorted({s.get("index", "?") for s in detail.get("saving_throws", [])})
    props["proficiencies"] = sorted({p.get("name", "?") for p in detail.get("proficiencies", [])})
    choices = []
    for choice in detail.get("proficiency_choices", []):
        item: dict = {"choose": choice.get("choose", 1)}
        if choice.get("type"):
            item["type"] = choice["type"]
        source = choice.get("from", {})
        if source.get("option_set_type") == "equipment_category":
            item["category"] = (source.get("equipment_category") or {}).get("name", "?")
        else:
            item["options"] = sorted(set(proficiency_option_names(source)))
        choices.append(item)
    if choices:
        props["proficiencyChoices"] = choices
    starting = [{"name": (s.get("equipment") or {}).get("name", "?"), "quantity": s.get("quantity", 1)}
                for s in detail.get("starting_equipment", [])]
    if starting:
        props["startingEquipment"] = starting
    start_options = []
    for choice in detail.get("starting_equipment_options", []):
        mapped_choice: dict = {"choose": choice.get("choose", 1)}
        mapped_choice.update(map_option_source(choice.get("from", {})))
        start_options.append(mapped_choice)
    if start_options:
        props["startingEquipmentOptions"] = start_options
    casting = detail.get("spellcasting")
    if casting:
        props["spellcasting"] = {"level": casting.get("level"),
                                 "ability": (casting.get("spellcasting_ability") or {}).get("index")}
    if spell_list:
        props["spellList"] = sorted(spell_list)
    subclasses = sorted({s.get("index", "?") for s in detail.get("subclasses", [])})
    if subclasses:
        props["subclasses"] = subclasses
    mapped_levels = map_levels(levels)
    if mapped_levels:
        props["levels"] = mapped_levels
    entry["properties"] = props
    entry["metadata"] = {"term": "class"}
    return entry


def map_subclass(detail: dict, levels: list) -> dict:
    # SRD holds exactly one subclass per class (Fiend for warlock, Evocation for
    # wizard, ...). Stored as a class entry with subclassOf so the creation wizard
    # filters subclasses by their parent class.
    entry: dict = {"id": detail["index"], "name": detail["name"]}
    props: dict = {"class": (detail.get("class") or {}).get("index", "?")}
    spells = []
    for spell in detail.get("spells", []):
        level = None
        for prereq in spell.get("prerequisites", []):
            match = re.search(r"(\d+)\s*$", prereq.get("name", ""))
            if match:
                level = int(match.group(1))
        spells.append({"spell": (spell.get("spell") or {}).get("index", "?"), "level": level})
    if spells:
        props["spells"] = spells
    mapped_levels = map_levels(levels)
    if mapped_levels:
        props["levels"] = mapped_levels
    entry["properties"] = props
    entry["metadata"] = {"term": "subclass", "subclassOf": (detail.get("class") or {}).get("index", "?")}
    return entry


def map_equipment(detail: dict, categories: list) -> dict:
    entry: dict = {"id": detail["index"], "name": detail["name"]}
    category = ((detail.get("equipment_category") or {}).get("index", "gear"))
    entry["tags"] = [category]
    props: dict = {"category": category}
    if categories:
        # Membership in the browsable equipment categories ("Martial Melee Weapons",
        # ...) so the creation wizard can resolve "choose any X" grants.
        props["categories"] = sorted(categories)
    cost = detail.get("cost")
    if cost:
        props["cost"] = {"quantity": cost.get("quantity", 0), "unit": cost.get("unit", "gp")}
    if detail.get("weight") is not None:
        props["weight"] = detail["weight"]
    damage = detail.get("damage")
    if damage:
        props["damage"] = {"dice": damage.get("damage_dice", "?"),
                           "type": (damage.get("damage_type") or {}).get("index", "?")}
    if detail.get("two_handed_damage"):
        props["twoHandedDamage"] = detail["two_handed_damage"].get("damage_dice", "?")
    weapon_range = detail.get("weapon_range")
    if weapon_range:
        props["weaponRange"] = weapon_range
    if detail.get("weapon_category"):
        props["weaponCategory"] = detail["weapon_category"]
    if detail.get("category_range"):
        props["categoryRange"] = detail["category_range"]
    if detail.get("range"):
        props["range"] = detail["range"]
    if detail.get("throw_range"):
        props["throwRange"] = detail["throw_range"]
    properties = detail.get("properties") or []
    if properties:
        props["properties"] = sorted({p.get("index", "?") for p in properties})
    armor_class = detail.get("armor_class")
    if armor_class:
        props["armorClass"] = armor_class
    if detail.get("armor_category"):
        props["armorCategory"] = detail["armor_category"]
    if detail.get("str_minimum") is not None:
        props["strengthMinimum"] = detail["str_minimum"]
    if detail.get("stealth_disadvantage") is not None:
        props["stealthDisadvantage"] = bool(detail["stealth_disadvantage"])
    contents = detail.get("contents") or []
    if contents:
        props["contents"] = [{"name": (c.get("item") or {}).get("name", "?"), "quantity": c.get("quantity", 1)}
                             for c in contents]
    if detail.get("quantity") is not None:
        props["quantity"] = detail["quantity"]
    entry["properties"] = props
    entry["metadata"] = {"term": "equipment"}
    return entry


def split_languages(value: object) -> list:
    if isinstance(value, list):
        # Language refs ({"index": ..., "name": ...}) or bare strings.
        return [(lang.get("name", "?") if isinstance(lang, dict) else str(lang)).strip()
                for lang in value]
    if isinstance(value, str):
        return [lang.strip() for lang in value.split(",")]
    return []


def map_race(detail: dict, subrace_ids: list) -> dict:
    entry: dict = {"id": detail["index"], "name": detail["name"]}
    props: dict = {}
    if detail.get("speed") is not None:
        props["speed"] = detail["speed"]
    if detail.get("size"):
        props["size"] = detail["size"]
    bonuses = {(b.get("ability_score") or {}).get("index", "?"): b.get("bonus", 0)
               for b in detail.get("ability_bonuses", [])}
    if bonuses:
        props["abilityBonuses"] = bonuses
    languages = split_languages(detail.get("languages"))
    if languages:
        props["languages"] = languages
    traits = sorted({t.get("name", "?") for t in detail.get("traits", [])})
    if traits:
        props["traits"] = traits
    if subrace_ids:
        props["subraces"] = sorted(subrace_ids)
    entry["properties"] = props
    entry["metadata"] = {"term": "race"}
    return entry


def map_subrace(detail: dict) -> dict:
    entry: dict = {"id": detail["index"], "name": detail["name"]}
    props: dict = {}
    bonuses = {(b.get("ability_score") or {}).get("index", "?"): b.get("bonus", 0)
               for b in detail.get("ability_bonuses", [])}
    if bonuses:
        props["abilityBonuses"] = bonuses
    traits = sorted({t.get("name", "?") for t in detail.get("traits", [])})
    if traits:
        props["traits"] = traits
    languages = split_languages(detail.get("languages"))
    if languages:
        props["languages"] = languages
    entry["properties"] = props
    entry["metadata"] = {"term": "subrace", "ancestryOf": (detail.get("race") or {}).get("index", "?")}
    return entry


# ------------------------------------------------------------------- pack io

ATTRIBUTION_TEMPLATE = """# Attribution for packs/{pack_id}

Mechanics data (names, numbers, enums, short factual labels) sourced from the
System Reference Document 5.1 ("SRD 5.1") by Wizards of the Coast LLC, via the
dnd5eapi projection at {api_base}, licensed under the Creative Commons
Attribution 4.0 International License
(https://creativecommons.org/licenses/by/4.0/legalcode).

No SRD prose is reproduced: spell/monster descriptions, higher-level text, and
action flavor text are dropped at import time (see scripts/import_srd51.py).
What remains are game facts - damage dice, ranges, areas, saving throws, stat
blocks - plus the entry names themselves.

Generated {generated_at} UTC: {spell_count} spells + {monster_count} monsters +
{class_count} classes/subclasses + {race_count} races/subraces + {gear_count} equipment.
Regenerate with: python3 scripts/import_srd51.py
"""


def write_pack(pack_dir: Path, spells: dict[str, dict], monsters: dict[str, dict],
               classes: dict[str, dict], races: dict[str, dict], equipment: dict[str, dict]) -> None:
    if pack_dir.exists():
        shutil.rmtree(pack_dir)
    for sub in ("spells", "actors", "classes", "ancestries", "equipment"):
        (pack_dir / sub).mkdir(parents=True)

    generated_at = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M")
    manifest = {
        "id": PACK_ID,
        "name": PACK_NAME,
        "version": "1.0.0",
        "systemId": SYSTEM_ID,
        "minAppVersion": "1.0.0",
        # Above the sample pack's 0: shared ids (fireball, goblin, ...) resolve to these
        # full-SRD entries deterministically wherever both packs are loaded.
        "priority": 10,
        "source": "SRD 5.1 via dnd5eapi (CC-BY-4.0)",
        "generatedAtUtc": generated_at,
        "counts": {"spells": len(spells), "monsters": len(monsters),
                   "classes": len(classes), "ancestries": len(races), "equipment": len(equipment)},
    }
    (pack_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (pack_dir / "ATTRIBUTION.md").write_text(
        ATTRIBUTION_TEMPLATE.format(
            pack_id=PACK_ID, api_base=API_BASE, generated_at=generated_at,
            spell_count=len(spells), monster_count=len(monsters),
            class_count=len(classes), race_count=len(races), gear_count=len(equipment)),
        encoding="utf-8")

    for slug in sorted(spells):
        write_entry(pack_dir / "spells" / f"{slug}.yaml", spells[slug])
    for slug in sorted(monsters):
        write_entry(pack_dir / "actors" / f"{slug}.yaml", monsters[slug])
    for slug in sorted(classes):
        write_entry(pack_dir / "classes" / f"{slug}.yaml", classes[slug])
    for slug in sorted(races):
        write_entry(pack_dir / "ancestries" / f"{slug}.yaml", races[slug])
    for slug in sorted(equipment):
        write_entry(pack_dir / "equipment" / f"{slug}.yaml", equipment[slug])


def validate_mapping(spells: dict[str, dict], monsters: dict[str, dict],
                     classes: dict[str, dict], races: dict[str, dict],
                     equipment: dict[str, dict]) -> None:
    problems: list[str] = []
    for slug, spell in spells.items():
        if not spell.get("id") or not spell.get("name"):
            problems.append(f"spell {slug}: missing id/name")
        level = (spell.get("metadata") or {}).get("level")
        if not isinstance(level, int) or not 0 <= level <= 9:
            problems.append(f"spell {slug}: bad level {level!r}")
        for effect in spell.get("effects") or []:
            params = effect.get("params") or {}
            if "dice" in params:
                if not re.fullmatch(r"\s*\d*d\d+(\s*[+-]\s*\d+)?\s*", str(params["dice"]), re.IGNORECASE):
                    problems.append(f"spell {slug}: unparseable dice {params['dice']!r}")
            elif "amount" not in params:
                problems.append(f"spell {slug}: effect has neither dice nor amount")
    for slug, monster in monsters.items():
        if not monster.get("id") or not monster.get("name"):
            problems.append(f"monster {slug}: missing id/name")
        hp = (monster.get("resources") or {}).get("HP")
        if not isinstance(hp, int) or hp < 1:
            problems.append(f"monster {slug}: bad HP {hp!r}")
    for slug, cls in classes.items():
        if not cls.get("id") or not cls.get("name"):
            problems.append(f"class {slug}: missing id/name")
        hit_die = (cls.get("properties") or {}).get("hitDie")
        is_subclass = (cls.get("metadata") or {}).get("subclassOf")
        if not is_subclass and hit_die not in (6, 8, 10, 12):
            problems.append(f"class {slug}: bad hitDie {hit_die!r}")
    for slug, race in races.items():
        if not race.get("id") or not race.get("name"):
            problems.append(f"race {slug}: missing id/name")
    for slug, gear in equipment.items():
        if not gear.get("id") or not gear.get("name"):
            problems.append(f"equipment {slug}: missing id/name")
    for slug, cls in classes.items():
        stack = [cls.get("properties") or {}]
        while stack:
            node = stack.pop()
            if isinstance(node, dict):
                stack.extend(node.values())
            elif isinstance(node, list):
                stack.extend(node)
            elif node == "?":
                problems.append(f"class {slug}: unmapped placeholder '?' in properties")
                break
    if problems:
        raise RuntimeError(f"{len(problems)} mapping problems:\n" + "\n".join(problems[:20]))


def main() -> int:
    parser = argparse.ArgumentParser(description="Import SRD 5.1 rules into packs/srd51-full.")
    parser.add_argument("--output", default=str(ROOT / "packs" / PACK_ID), help="Pack directory to write.")
    parser.add_argument("--no-install", action="store_true", help="Skip mirroring the pack into plugins/ (dev runtime dir).")
    parser.add_argument("--limit", type=int, default=0, help="Import at most N entries per dataset (smoke test).")
    args = parser.parse_args()

    print("Fetching 5e spell details...")
    spell_details = fetch_all_details("/api/spells", "spells")
    print("Fetching 5e monster details...")
    monster_details = fetch_all_details("/api/monsters", "monsters")
    print("Fetching 5e class details...")
    class_details = fetch_all_details("/api/classes", "classes")
    print("Fetching 5e subclass details...")
    subclass_details = fetch_all_details("/api/subclasses", "subclasses")
    print("Fetching 5e equipment details...")
    equipment_details = fetch_all_details("/api/equipment", "equipment")
    print("Fetching 5e race details...")
    race_details = fetch_all_details("/api/races", "races")
    print("Fetching 5e subrace details...")
    subrace_details = fetch_all_details("/api/subraces", "subraces")

    if args.limit:
        spell_details = dict(sorted(spell_details.items())[:args.limit])
        monster_details = dict(sorted(monster_details.items())[:args.limit])
        class_details = dict(sorted(class_details.items())[:args.limit])
        subclass_details = dict(sorted(subclass_details.items())[:args.limit])
        equipment_details = dict(sorted(equipment_details.items())[:args.limit])
        race_details = dict(sorted(race_details.items())[:args.limit])
        subrace_details = dict(sorted(subrace_details.items())[:args.limit])

    print("Fetching equipment categories...")
    categories_index = fetch_json(f"{API_BASE}/api/equipment-categories")["results"]
    item_categories: dict[str, list] = {}
    with ThreadPoolExecutor(max_workers=12) as pool:
        cat_futures = {pool.submit(fetch_json, f"{API_BASE}{c['url']}"): c for c in categories_index}
        for future in as_completed(cat_futures):
            category = future.result()
            for member in category.get("equipment", []):
                item_categories.setdefault(member["index"], []).append(category["name"])

    print("Fetching class levels + spell lists...")
    class_levels: dict[str, list] = {}
    class_spells: dict[str, list] = {}
    subclass_levels: dict[str, list] = {}
    with ThreadPoolExecutor(max_workers=12) as pool:
        level_futures = {pool.submit(fetch_json_any, f"{API_BASE}{d['url']}/levels"): ("class", slug)
                         for slug, d in class_details.items()}
        level_futures.update({pool.submit(fetch_json_any, f"{API_BASE}{d['url']}/levels"): ("subclass", slug)
                              for slug, d in subclass_details.items()})
        spell_futures = {pool.submit(fetch_json_any, f"{API_BASE}{d['url']}/spells"): slug
                         for slug, d in class_details.items()
                         if d.get("spells")}
        for future in as_completed(level_futures):
            kind, slug = level_futures[future]
            (class_levels if kind == "class" else subclass_levels)[slug] = future.result()
        for future in as_completed(spell_futures):
            result = future.result()
            results = result.get("results", []) if isinstance(result, dict) else []
            class_spells[spell_futures[future]] = [r.get("index", "?") for r in results]

    print("Mapping to Codex YAML...")
    spells = {slug: map_spell(detail) for slug, detail in spell_details.items()}
    monsters = {slug: map_monster(detail) for slug, detail in monster_details.items()}
    classes = {slug: map_class(detail, class_levels.get(slug, []), class_spells.get(slug, []))
               for slug, detail in class_details.items()}
    classes.update({slug: map_subclass(detail, subclass_levels.get(slug, []))
                    for slug, detail in subclass_details.items()})
    subraces_by_race: dict[str, list] = {}
    for slug, detail in subrace_details.items():
        subraces_by_race.setdefault((detail.get("race") or {}).get("index", "?"), []).append(slug)
    races = {slug: map_race(detail, subraces_by_race.get(slug, []))
             for slug, detail in race_details.items()}
    races.update({slug: map_subrace(detail) for slug, detail in subrace_details.items()})
    equipment = {slug: map_equipment(detail, item_categories.get(slug, []))
                 for slug, detail in equipment_details.items()}
    validate_mapping(spells, monsters, classes, races, equipment)

    pack_dir = Path(args.output)
    write_pack(pack_dir, spells, monsters, classes, races, equipment)
    print(f"Wrote {len(spells)} spells + {len(monsters)} monsters + {len(classes)} classes + "
          f"{len(races)} ancestries + {len(equipment)} equipment to {pack_dir}")

    if not args.no_install:
        # The dev-time PluginLoader reads the repo-root plugins/ directory, not packs/.
        dest = ROOT / "plugins" / PACK_ID
        if dest.exists():
            shutil.rmtree(dest)
        shutil.copytree(pack_dir, dest)
        print(f"Mirrored to {dest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
