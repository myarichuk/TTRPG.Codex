#!/usr/bin/env python3 -I
"""Deterministic audit of the pinned SRD 5.2.1 source (5e-bits JSON) for text the data cannot hold yet.

Re-run:  python3 -I audit.py [--src DIR] [--pack DIR] [--out DIR]
Defaults: src = ./src (5e-SRD-*.json at the pinned commit, see tools/Codex.ContentImport/SrdDownloader.cs),
          pack = the repo's content/systems/srd5e/packs/srd-5.2.1 (only read, for the spells' `use` coverage),
          out  = the directory of this script.
Writes flagged.jsonl and summary.json. Counts are from regexes, not ground truth.
"""
import argparse, collections, json, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument('--src', default=os.path.join(HERE, 'src'))
ap.add_argument('--pack', default=os.path.join(HERE, '../../', 'content/systems/srd5e/packs/srd-5.2.1'))
ap.add_argument('--tables', default=os.path.join(HERE, '../../', 'tools/Codex.ContentImport/Data/srd521-class-tables.json'))
ap.add_argument('--out', default=HERE)
args = ap.parse_args()


def load(name):
    p = os.path.join(args.src, f'5e-SRD-{name}.json')
    return json.load(open(p)) if os.path.exists(p) else []


def clean(t):
    t = (t or '').replace('­', '')
    t = re.sub(r'(?<=[a-z])- (?=[a-z])', '', t)  # transcription hyphenation: "Blud- geoning"
    return re.sub(r'\s+', ' ', t).strip()


def snip(text, m, before=7, after=12):
    """<=20 words around a match, self-contained."""
    s, e = m.start(), m.end()
    pre = text[:s].split()[-before:]
    mid = text[s:e].split()
    post = text[e:].split()[:after]
    words = pre + mid + post
    if len(words) > 20:
        room = max(0, 20 - len(mid))
        b = min(len(pre), room // 3)
        a = room - b
        words = pre[len(pre) - b:] + mid[:20] + post[:a]
        words = words[:20]
    return ' '.join(words)


flagged = []
counts = collections.OrderedDict()   # category -> dict(entries=set)
examples = {}


def flag(cat, entry, etype, text, m, sub=None, note=None):
    key = (cat, sub)
    counts.setdefault(key, {})[(entry, etype)] = counts.setdefault(key, {}).get((entry, etype), 0) + 1
    rec = {'category': cat + (f':{sub}' if sub else ''), 'entry': entry, 'type': etype, 'snippet': snip(text, m)}
    if note:
        rec['note'] = note
    # one record per (category, entry) keeps the file sortable
    k2 = (rec['category'], entry, etype)
    if k2 not in flag.seen:
        flag.seen.add(k2)
        flagged.append(rec)


flag.seen = set()


def flag_plain(cat, entry, etype, snippet, sub=None, note=None):
    key = (cat, sub)
    counts.setdefault(key, {})[(entry, etype)] = 1
    rec = {'category': cat + (f':{sub}' if sub else ''), 'entry': entry, 'type': etype, 'snippet': snippet}
    if note:
        rec['note'] = note
    k2 = (rec['category'], entry, etype)
    if k2 not in flag.seen:
        flag.seen.add(k2)
        flagged.append(rec)


# ------------------------------------------------------------------ pack `use` coverage (read only)
def pack_use():
    use = {}
    d = os.path.join(args.pack, 'spells')
    if not os.path.isdir(d):
        return None
    for f in os.listdir(d):
        if not f.endswith('.yaml'):
            continue
        sid = f[:-5]
        lines = open(os.path.join(d, f)).read().split('\n')
        block = None
        for i, l in enumerate(lines):
            if l == '  use:':
                block = {}
                for l2 in lines[i + 1:]:
                    if not l2.startswith('    '):
                        break
                    mm = re.match(r'^    (\w+): ?(.*)$', l2)
                    if mm:
                        block[mm.group(1)] = mm.group(2)
                break
        use[sid] = block
    return use


PACK_USE = pack_use()

# ------------------------------------------------------------------ spells
DICE = re.compile(r'\b(\d+d\d+)(?:\s*[+−-]\s*\d+)?')
spells = load('Spells')
summary = {'sources': {}}
summary['sources']['spells'] = len(spells)

HEALCTX = re.compile(r'Hit Points?|healing|regain', re.I)
for sp in spells:
    name, sid = sp['name'], sp['index']
    desc = clean(sp.get('description'))
    higher = clean(sp.get('higher_level')) if isinstance(sp.get('higher_level'), str) else clean(' '.join(sp.get('higher_level') or []))
    full = desc + (' ' + higher if higher else '')
    use = PACK_USE.get(sid) if PACK_USE is not None else None
    has_use = bool(use)

    # dice expressions in the base text (not the slot-scaling paragraph)
    base = re.sub(r'(?:Cantrip Upgrade|Using a Higher-Level Spell Slot|Higher-Level Spell Slot)\.[^\n]*', '', desc)
    base = re.sub(r'[^.]*\b(?:increases? by|reach(?:es)? levels? 5)[^.]*\.?', '', base)
    dice_all = [(m.group(1), m) for m in DICE.finditer(desc) if m.group(1) in {x.group(1) for x in DICE.finditer(base)}]
    dice_all = [(d, m) for d, m in dice_all if not any(abs(m.start() - dd.start()) < 0 for dd in [])]
    # damage/heal-ish dice
    def dmg_ctx(m):
        after = desc[m.end():m.end() + 45]
        before = desc[max(0, m.start() - 45):m.start()]
        return bool(re.search(r'damage|Hit Points', after, re.I)) or bool(re.search(r'(damage|Hit Points|regains?) (equal to|of)? ?(a number of)?\s*$', before, re.I)) or bool(re.search(r'(damage|Hit Points) equal to\s*$', before))
    dmg_dice = [(d, m) for d, m in dice_all if dmg_ctx(m)]
    distinct_dmg = list(dict.fromkeys(d for d, _ in dmg_dice))
    distinct_all = list(dict.fromkeys(d for d, _ in dice_all))
    if len(distinct_dmg) >= 2:
        captured = use.get('damage') if use else None
        flag('spell.multi_roll_damage', name, 'spell', desc, dmg_dice[1][1], sub=('captured_first_only' if (use and use.get('damage') and not use.get('damage') == distinct_dmg[-1]) else 'no_use' if not use else 'captured_other'),
             note=f"distinct damage/heal dice {distinct_dmg}; use.damage in pack = {captured}")
    elif len(distinct_all) >= 2:
        flag('spell.multi_dice_non_damage', name, 'spell', desc, dice_all[1][1], note=f"distinct dice {distinct_all}")

    # beams / darts / projectiles
    for m in re.finditer(r'\b(two|three|four|five|six|seven|eight|nine|ten|\d+) (?:\w+ ){0,2}?(darts?|rays?|beams?|bolts?|missiles?|motes?|projectiles?|bursts?)\b', desc, re.I):
        flag('spell.projectile_count', name, 'spell', desc, m)
        break
    else:
        m = re.search(r'\b(creates?|shoots?|hurls?|launch\w*) (?:a number of |several |multiple )?(darts?|rays?|beams?|bolts?|missiles?|motes?)\b|\bnumber of (darts|rays|beams|bolts|missiles)\b|\b(beams?|rays?|darts?|missiles?) of (?:\w+ ){0,2}(?:light|energy|force|fire)\b', desc, re.I)
        if m:
            flag('spell.projectile_count', name, 'spell', desc, m, sub='unnamed_count')
    m = re.search(r'(?:one|two|three|an?) (?:additional|more|extra) (target|creature|dart|ray|beam|bolt|missile|mote|Beast|Humanoid|object)s?\b|(?:additional|more|extra) (?:targets?|creatures?|darts?|rays?|beams?|bolts?)\b|target (?:one|two|up to \w+) (?:additional|more)|(?:creates?|create|makes?) (?:one|an) (?:more|additional) (?:dart|ray|beam)', higher, re.I)
    if m:
        flag('spell.scaling_adds_targets_or_beams', name, 'spell', higher, m, sub='slot_level')
    m = re.search(r'creates? (?:two|three|four) beams|(?:two|three|four) (?:beams|rays|darts) at level', desc, re.I)
    if m and sp.get('level') == 0:
        flag('spell.scaling_adds_targets_or_beams', name, 'spell', desc, m, sub='cantrip_levels')

    # flat healing: a number of Hit Points, no dice
    for m in re.finditer(r'(regains?|restores?|restoring|gains?|heals?|healing)\s+(?:up to\s+|a total of\s+)?(\d+)\s+(Temporary )?Hit Points', desc):
        flag('spell.flat_healing', name, 'spell', desc, m, sub='temp_hp' if m.group(3) else 'healing')
        break
    m = re.search(r'Hit Points (?:equal to|of) (\d+)\b(?! ?d)', desc)
    if m:
        flag('spell.flat_healing', name, 'spell', desc, m, sub='hp_threshold_or_pool')

    # damage type chosen at casting
    m = re.search(r"damage (?:of|that is of) (?:the|that|a) (?:chosen |spirit's |selected )?type|damage type (?:of your choice|you choose|you chose)|(?:chosen|selected) damage type[^.]{0,40}(?:damage|takes|deals)|(?:deals?|takes?) [^.]{0,30}damage of the type|of the type in parentheses|(?:damage|deals?) [^.]{0,30}(?:type|types) (?:that )?you (?:choose|chose)|damage type of your choice|determines its damage type", full, re.I)
    if m and re.search(r'Resistance', full[max(0, m.start() - 70):m.end() + 40]):
        flag('spell.damage_type_chosen', name, 'spell', full, m, sub='resistance_not_damage')
    elif m or re.search(r'damage \(your choice', full):
        flag('spell.damage_type_chosen', name, 'spell', full, m or re.search(r'damage \(your choice', full), sub='damage_dealt')
    else:
        m = re.search(r'Resistance to (?:one|the) (?:chosen )?damage type|choose a damage type|one damage type (?:of your choice|that you choose)|damage type you choose', full, re.I)
        if m:
            flag('spell.damage_type_chosen', name, 'spell', full, m, sub='resistance_not_damage')

    # the spells without `use`
    if PACK_USE is not None and not has_use:
        bucket = None
        any_dice = bool(DICE.search(full))
        flat = re.search(r'\b(\d+) (?:\w+ )?damage\b|damage equal to (?:\w+ ){0,6}?(\d+)|\b(\d+) (Temporary )?Hit Points|Hit Points (?:equal to|of) \d+|\bdeals? (\d+)\b', desc)
        if not any_dice and not flat:
            m = re.search(r'.', desc)
            bucket = 'no_dice_summon_or_stat_block' if re.search(r'^(Summon|Conjure|Animate|Find |Giant Insect|Simulacrum|Planar Ally|Create Undead|Awaken)', name) else 'no_dice_no_number'
            snippet_m = re.search(r'.{0,1}', desc)
            flag('spell.no_use_bucket', name, 'spell', desc, re.search(r'\w+', desc), sub=bucket,
                 note='no roll in the rules text (utility, summon, buff)')
        elif not any_dice:
            flag('spell.no_use_bucket', name, 'spell', desc, flat, sub='flat_numbers')
        else:
            # dice present but UseData did not take them
            if not dmg_dice:
                if re.search(r'adds? (?:a )?\d+d\d+ to|reduces? the total damage[^.]{0,20}\d+d\d+|gains? \d+d\d+ Temporary', full): bucket = 'dice_bonus_to_rolls'
                elif re.search(r'reach levels? 5|levels? 5 \(', full): bucket = 'dice_cantrip_scaling_only'
                else: bucket = 'dice_duration_count_or_table'
                flag('spell.no_use_bucket', name, 'spell', full, DICE.search(full), sub=bucket)
            else:
                m = dmg_dice[0][1]
                if re.search(r'Hit Points', desc[m.end():m.end() + 45]) or re.search(r'(regains?|restores?)[^.]{0,40}Hit Points', desc):
                    bucket = 'dice_healing_or_hp_pool_shape'
                elif re.search(r'(damage|Hit Points) equal to', desc) or re.search(r'for each|per ', desc[max(0, m.start() - 60):m.end() + 60]):
                    bucket = 'dice_scaled_by_count_or_equal_to'
                elif re.search(r'\d+d\d+ (plus|\+) ', desc[m.start():m.end() + 30]):
                    bucket = 'dice_plus_modifier_shape'
                else:
                    bucket = 'dice_other_shape'
                flag('spell.no_use_bucket', name, 'spell', desc, m, sub=bucket)

# ------------------------------------------------------------------ features, traits, feats, backgrounds
corpus = []  # (name, type, text)
seen_text = set()


def add_corpus(name, etype, text):
    t = clean(text)
    if not t:
        return
    k = (name, t)
    if k in seen_text:
        return
    seen_text.add(k)
    corpus.append((name, etype, t))


subs = load('Subclasses')
sub_pairs = {(f['name'], clean(f.get('description'))) for s_ in subs for f in s_.get('features', [])}
feat_pairs = set()
for f in load('Features'):
    t = clean(f.get('description'))
    lvl = f.get('level', {}).get('name', '').split()[-1] if f.get('level') else ''
    feat_pairs.add((f['name'], t))
    et = 'subclass-feature' if (f['name'], t) in sub_pairs else 'class-feature'
    corpus.append((f"{f['name']} ({f['class']['name']} {lvl})".replace('  ', ' '), et, t)) if t else None
for s_ in subs:
    for f in s_.get('features', []):
        t = clean(f.get('description'))
        if t and (f['name'], t) not in feat_pairs:
            corpus.append((f"{f['name']} ({s_['name']} {f.get('level')})", 'subclass-feature', t))
for t in load('Traits'):
    sp = ', '.join(x['name'] for x in t.get('species', []))
    add_corpus(f"{t['name']} ({sp})", 'species-trait', t.get('description'))
for t in load('Feats'):
    add_corpus(t['name'], f"feat:{t.get('type')}", t.get('description'))
bgs = load('Backgrounds')
for b in bgs:
    add_corpus(b['name'], 'background', b.get('description'))
summary['sources']['features'] = sum(1 for c in corpus if c[1] == 'class-feature')
summary['sources']['subclass_features'] = sum(1 for c in corpus if c[1] == 'subclass-feature')
summary['sources']['traits'] = sum(1 for c in corpus if c[1] == 'species-trait')
summary['sources']['feats'] = sum(1 for c in corpus if c[1].startswith('feat'))
summary['sources']['backgrounds_with_text'] = sum(1 for c in corpus if c[1] == 'background')

CHOICE = re.compile(r'of your choice|you choose|choose|chosen|your choice|one of the following|any combination|of the same type|that damage type|that type|as determined by|associated with your|another skill|one extra', re.I)


def sentence_at(text, m):
    s = max(text.rfind('. ', 0, m.start()), text.rfind('\n', 0, m.start()))
    e = text.find('. ', m.end())
    return text[s + 1 if s >= 0 else 0: e + 1 if e >= 0 else len(text)]


def fixed_or_choice(text, m):
    sent = sentence_at(text, m)
    return 'choice' if CHOICE.search(sent) else 'fixed'


R = lambda p, fl=0: re.compile(p, fl)
CATS = [
    # proficiency
    ('feat.proficiency', R(r"\bgain proficiency\b|\b(?:you|You) (?:have|gain|are given|become|also gain|now have) (?:\w+ ){0,2}?proficien(?:cy|t)\b(?! Bonus)(?! (?:in|with) (?:them|it)\b)|\bproficiency (?:in|with) (?!them\b|it\b|that\b|those\b|the chosen|any of the)|\b(?:gain )?training (?:in|with) (?:Heavy|Medium|Light|Martial|Shields?|\w+ armor)|\bbecome proficient\b|\bproficient in\b|\bproficiency in all\b"), 'fc'),
    ('feat.expertise', R(r'\bExpertise\b|double your Proficiency Bonus|twice your Proficiency Bonus|Proficiency Bonus is doubled'), None),
    ('feat.resistance', R(r'\bResistance(?:s)? to (?!the (?:damage|Bludgeoning, Piercing, and Slashing) you|damage from)[^.]{0,80}?(?:damage|\.)|\bresistan\w+ to the (?:damage|chosen)|you have resistance'), 'fc'),
    ('feat.immunity_damage', R(r'\bImmun(?:e|ity) to (?:Poison|Fire|Cold|Lightning|Acid|Necrotic|Radiant|Thunder|Psychic|Force|Bludgeoning|Piercing|Slashing|damage|the damage)\w*[^.]{0,40}'), 'fc'),
    ('feat.immunity_condition', R(r'\bImmun(?:e|ity) to (?:the )?(?:Charmed|Frightened|Poisoned|Paralyzed|Stunned|Blinded|Deafened|Exhaustion|Grappled|Prone|Restrained|Disease|Incapacitated)\b|\bcan\'t be (?:Charmed|Frightened|Poisoned|Paralyzed|Stunned)\b'), 'fc'),
    ('feat.asi_from_feature', R(r'(?:\b(?:Strength|Dexterity|Constitution|Intelligence|Wisdom|Charisma|ability) scores?\b[^.]{0,50}\b(?:increases?|is increased|each increase)\b)|(?:\bincrease (?:your|one|two|a|the|that)\b[^.]{0,40}\bscores?\b)|(?:\bmaximum (?:of|is) (?:20|22|25|30)\b)|(?:\bscores? (?:to|can exceed) (?:20|25|30)\b)|(?:\bincrease(?:s)? by 1\b[^.]{0,30}(?:Strength|Dexterity|Constitution|Intelligence|Wisdom|Charisma))'), 'fc'),
    ('feat.free_casting.without_slot', R(r'without (?:expending|using|spending|a|any|the use of|requiring)\b[^.]{0,20}\b(?:spell )?slot|without a spell slot|no spell slot|doesn\'t require a spell slot|without \w+ spell slot'), 'fc'),
    ('feat.free_casting.at_will', R(r'\bat will\b|can cast [^.]{0,60}\bwithout\b[^.]{0,30}\b(?:Material|components)\b'), 'fc'),
    ('feat.free_casting.once_per_rest', R(r'(?:cast|casting) [^.]{0,80}\b(?:once|a number of times)\b[^.]{0,80}\b(?:Long|Short) Rest|(?:Long|Short) Rest[^.]{0,60}cast (?:it|the spell|that spell|this spell)|cast [^.]{0,60}\bonce\b[^.]{0,60}(?:finish|until)|\bonce per (?:Long|Short) Rest\b[^.]{0,80}\bspell|Spell[^.]{0,50}\bonce per (?:Long|Short) Rest'), 'fc'),
    ('feat.free_casting.ritual', R(r'\bas a Ritual\b|\bRitual (?:Adept|spell)|Ritual tag|\bcast [^.]{0,40}\bRitual\b|\bRitual\b'), None),
    ('feat.option_menu', R(r'(?:choose|select|pick) (?:one|two|three|four|five|a|an|\d) (?:\w+ ){0,5}?(?:of the following|from (?:the|this) (?:following )?list|option|options|below)|one of the following|\bchoose (?:one|two|three|a|an|your)\b[^.]{0,30}(?:Option|each time|Aspect|Tradition|Style|Order|Domain|Pact|Metamagic|Maneuver|Invocation|ancestry|type|land)|\bYour choice\b|following options'), None),
    ('feat.spend_points', R(r'\b(?:spend|spends|expend|expends|spending|expending)\s+(?:up to |at least |a number of |any number of |X |\d+ |one |two |three |four |five |six |a |an |one or more |each |1 |2 |3 )?(?:of your )?(?:\w+\s){0,2}?(?:Sorcery Points?|Focus Points?|Ki Points?|Superiority Dice|Superiority Die|Hit Point Dice?|Hit Dice|Bardic Inspiration|Channel Divinity|Wild Shape|Rage|Lay On Hands|Hit Points from|\w+ Points?|uses?|Dice|Die)\b'), 'spend'),
    ('feat.pool_of', R(r'\bpool of (?:healing|Hit Points|\w+)|\bpool is replenished|a total of \d+ (?:times|Hit Points) your\b|Lay On Hands|restore (?:a number of )?Hit Points[^.]{0,30}pool'), None),
    ('feat.owned_dice', R(r'\b(?:Bardic Inspiration|Martial Arts|Superiority|Sneak Attack|Psionic Energy|Rage Damage|Fighting Spirit|Inspiration) (?:die|dice|damage)\b|\b[A-Z][a-z]+(?: [A-Z][a-z]+)? Die\b(?! per)|\b(?:the|your) \w+ die (?:is|becomes|changes)|roll a d\d+|\bd\d+ (?:equals|you roll|and add)'), None),
    ('feat.dice_level_scaling', R(r'(?:becomes|changes to|increases to|rises to|is) (?:a |an )?(?:d\d+|\d+d\d+)\b[^.]{0,40}level|\blevel \d+ \(\d+d\d+\)|\bat (?:Barbarian|Bard|Cleric|Druid|Fighter|Monk|Paladin|Ranger|Rogue|Sorcerer|Warlock|Wizard|\w+) (?:levels? )?\d+[^.]{0,80}\bd\d+|\bwhen you reach (?:\w+ )?level \d+[^.]{0,50}\bd\d+|\blevel \d+[^.]{0,30}\(\d+d\d+\)|d\d+ at (?:level|\w+ level) \d+|(?:Level|level) \d+: (?:\d+)?d\d+|increases? (?:to|by) (?:\d+)?d\d+ (?:at|when|on)|\d+d\d+ (?:at|when you reach) (?:\w+ )?level|\d+d\d+ [A-Za-z]+ damage[^.]{0,40}(?:at|when you reach) (?:\w+ )?level \d+'), None),
    ('feat.extra_attack', R(r'\bExtra Attack\b|attack (?:twice|three times|four times)|\bmake (?:two|three|four) attacks\b|replace one of those attacks'), None),
    ('feat.crit_range', R(r'\b(?:19|18)(?:–|-| or )20\b|Critical Hit (?:on|with|if)[^.]{0,30}(?:roll|natural)|score a Critical Hit on a'), None),
    ('feat.attack_damage_bonus', R(r'[+➕]\d+ (?:bonus )?to (?:attack|damage)|bonus to (?:attack|damage) rolls|add your [^.]{0,30}modifier to the damage|add your [^.]{0,30}to (?:the )?damage|(?:attack|damage) rolls?[^.]{0,40}\+\d|extra (?:\d*d\d+ )?(?:[A-Z][a-z]+ )?damage|additional (?:\d*d\d+ )?(?:[A-Z][a-z]+ )?damage|deal(?:s)? an extra|bonus to the damage|damage bonus|Rage Damage|add (?:your|the) [^.]{0,30}(?:modifier|bonus) to (?:the )?(?:damage|attack)|\+\d to (?:the )?(?:attack|damage)|bonus to the damage|(?:Attack|attack) rolls? (?:you|with)[^.]{0,40}\+\d'), None),
    ('feat.gear_condition', R(r'(?:while|if|when|as long as|so long as)\b[^.]{0,30}\b(?:wearing|wield|wielding|holding|carrying|unarmored|wear|armed)\b|aren\'t (?:wearing|wielding)|are(?:n\'t| not) wearing|not wearing|wearing no |no armor|in two hands|with two hands|one-handed|Light armor|Medium armor|Heavy armor|using (?:a|an|your) (?:Shield|Simple|Martial|Quarterstaff|Melee|ranged|Finesse|Heavy|Light|Thrown)'), None),
    ('feat.reactive.zero_hp', R(r'\b(?:drop|dropped|dropping|reduced|would be reduced|fall|falls) to 0 Hit Points|instead drop to 0|when you would drop'), None),
    ('feat.reactive.reaction', R(r'\bReaction\b|as a reaction'), None),
    ('feat.reactive.when_hit', R(r'\b(?:when|whenever) (?:a creature|an? \w+|you|another creature|the target|an attacker|someone)\b[^.]{0,40}\b(?:hits|hit|attacks|damages|deals damage|takes damage|fails|succeeds|makes an attack|would)\b'), None),
    ('ref.speed', R(r'\bSpeed (?:increases?|is increased|is \d+)|\+\d+ feet|\bSpeed by \d+|Climb Speed|Swim Speed|Fly Speed|Fly(?:ing)? Speed|Burrow Speed|equal to your Speed|your Speed increases'), None),
    ('ref.darkvision', R(r'\bDarkvision\b'), None),
    ('ref.armor_class', R(r'\bArmor Class (?:equals|is|increases|of|can|becomes)|\+\d+ bonus to (?:your )?(?:AC|Armor Class)|base Armor Class|Armor Class by \d|bonus to (?:your )?Armor Class|AC (?:equals|is|increases)'), None),
]

for name, etype, text in corpus:
    for cat, rx, mode in CATS:
        m = rx.search(text)
        if not m:
            continue
        sub = None
        if mode == 'fc':
            sub = fixed_or_choice(text, m)
            # an "of your choice" damage type etc. on resistance
        elif mode == 'spend':
            pool = re.sub(r'^(?:up to |at least |a number of |any number of |X |\d+ |one |two |three |four |five |six |a |an |one or more |each |of your )+', '', m.group(0), flags=re.I)
            sub = None
        flag(cat, name, etype, text, m, sub=sub)
        # kind split for proficiency
        if cat == 'feat.proficiency':
            sent = sentence_at(text, m)
            kinds = []
            if re.search(r'saving throw', sent, re.I): kinds.append('saves')
            if re.search(r'\bskills?\b|Acrobatics|Animal Handling|Arcana|Athletics|Deception|History|Insight|Intimidation|Investigation|Medicine|Nature|Perception|Performance|Persuasion|Religion|Sleight|Stealth|Survival', sent): kinds.append('skills')
            if re.search(r'\barmor\b|\bShields?\b', sent, re.I): kinds.append('armor')
            if re.search(r'\bweapons?\b|Martial|Simple|Longsword|Rapier|Shortsword|Longbow|Scimitar|Greatsword|Quarterstaff|Crossbow|Dagger', sent): kinds.append('weapons')
            if re.search(r'\btools?\b|supplies|kit\b|instrument|Thieves|Herbalism|Poisoner|Disguise|Forgery|Navigator|Artisan', sent, re.I): kinds.append('tools')
            if re.search(r'\blanguages?\b', sent, re.I): kinds.append('languages')
            for k in kinds or ['other']:
                counts.setdefault(('feat.proficiency.kind', k + '/' + sub), {})[(name, etype)] = 1
        if cat == 'feat.spend_points':
            sent = sentence_at(text, m)
            pools = ['Focus Point', 'Sorcery Point', 'Channel Divinity', 'Wild Shape', 'Bardic Inspiration', 'Rage', 'Second Wind', 'Lay On Hands', 'Superiority', 'Hit Point Die', 'Hit Point']
            key = next((pl for pl in pools if pl.lower() in sent.lower()), 'other')
            if key == 'Hit Point' and 'pool' in sent: key = 'Lay On Hands'
            counts.setdefault(('feat.spend_points.pool', key), {})[(name, etype)] = 1

# ability-score text inside backgrounds / feats is structured in the source; count structured ASI in feats' text anyway.

# eldritch / option-menu presence
all_text = '\n'.join(t for _, _, t in corpus)
option_names = {
    'Eldritch Invocations': ['Agonizing Blast', 'Armor of Shadows', 'Pact of the Blade', 'Pact of the Chain', 'Pact of the Tome', 'Devil\'s Sight', 'Lessons of the First Ones', 'Repelling Blast', 'Fiendish Vigor', 'Lifedrinker', 'Gaze of Two Minds', 'Mask of Many Faces', 'Misty Visions', 'One with Shadows', 'Thirsting Blade', 'Eldritch Mind', 'Eldritch Smite', 'Investment of the Chain Master', 'Master of Myriad Forms', 'Cloak of Flies', 'Ascendant Step', 'Otherworldly Leap', 'Whispers of the Grave', 'Gift of the Depths', 'Visions of Distant Realms'],
    'Metamagic': ['Careful Spell', 'Distant Spell', 'Empowered Spell', 'Extended Spell', 'Heightened Spell', 'Quickened Spell', 'Seeking Spell', 'Subtle Spell', 'Transmuted Spell', 'Twinned Spell'],
    'Maneuvers': ['Riposte', 'Parry', 'Trip Attack', 'Menacing Attack', 'Precision Attack', 'Commander\'s Strike', 'Disarming Attack', 'Goading Attack'],
    'Fighting Styles': ['Archery', 'Defense', 'Great Weapon Fighting', 'Two-Weapon Fighting', 'Two Weapon Fighting', 'Blessed Warrior', 'Druidic Warrior', 'Dueling', 'Protection', 'Interception', 'Thrown Weapon Fighting', 'Unarmed Fighting', 'Blind Fighting'],
}
menu = {}
entry_names = {n.split(' (')[0].lower() for n, _, _ in corpus}
for group, opts in option_names.items():
    rows = []
    for o in opts:
        as_entry = o.lower() in entry_names
        in_text = [n for n, _, t in corpus if o.lower() in t.lower() and n.split(' (')[0].lower() != o.lower()]
        rows.append({'option': o, 'is_own_entry': as_entry, 'mentioned_in': in_text[:3]})
    menu[group] = rows
summary['option_menus'] = menu

# ------------------------------------------------------------------ class tables
levels = load('Levels')
cs = collections.defaultdict(lambda: collections.defaultdict(int))
for l in levels:
    for k, v in (l.get('class_specific') or {}).items():
        cs[l['class']['index']][k] += 1
tables = json.load(open(args.tables))['classes']
table_keys = set()
for c in tables.values():
    for lv in c['levels']:
        table_keys |= set(lv.keys())
summary['class_tables'] = {
    'columns_in_levels_source_class_specific': {c: dict(v) for c, v in cs.items()},
    'keys_in_srd521_class_tables_json': sorted(table_keys),
}

# ------------------------------------------------------------------ monsters
mons = load('Monsters')
summary['sources']['monsters'] = len(mons)
MAIN = re.compile(r'^(Melee|Ranged|Melee or Ranged) Attack Roll: \+(\d+)(?: \([^)]*\))?, (?:reach (\d+) ft\.(?: or range (\d+)/(\d+) ft\.)?|range (\d+)(?:/(\d+))? ft\.)(?: [^H][^.]*?\.)? Hit: (\d+) \((\d+d\d+(?: [+−-] \d+)?)\) ([A-Z][a-z]+) damage')
DMGCL = re.compile(r'(\d+) \((\d+d\d+(?: [+−-] \d+)?)\) ([A-Z][a-z]+)(?: \([^)]*\))? damage')
mon_stats = collections.Counter()
mon_ex = collections.defaultdict(list)
mon_actions_total = 0
structured_ok = 0
for mo in mons:
    for sect in ('actions', 'bonus_actions', 'reactions', 'legendary_actions'):
        for a in mo.get(sect) or []:
            d = clean(a.get('desc'))
            nm = f"{mo['name']}: {a['name']}"
            mon_actions_total += 1
            if not re.match(r'^(Melee|Ranged|Melee or Ranged) Attack Roll:', d):
                if re.search(r'Saving Throw:', d): mon_stats['save_action'] += 1
                elif a['name'] == 'Multiattack': mon_stats['multiattack'] += 1
                else: mon_stats['other_non_attack'] += 1
                continue
            mon_stats['attack_prose'] += 1
            if a.get('damage') and a.get('attack_bonus') is not None:
                structured_ok += 1
            m = MAIN.match(d)
            hit = d.split('Hit:', 1)[1] if 'Hit:' in d else ''
            dmgs = DMGCL.findall(hit)
            first = hit.split('. ')[0]
            tags = []
            if m: mon_stats['one_regex_match'] += 1
            else: tags.append('main_regex_fail')
            if len(DMGCL.findall(first)) > 1 or re.search(r'\) [A-Z][a-z]+ damage plus', first):
                tags.append('two_or_more_damage_types_plus')
            if re.search(r'\bplus\b', first) and 'two_or_more_damage_types_plus' not in tags: tags.append('plus_other')
            if re.search(r'if used with two hands|two hands|Versatile', hit, re.I) or re.search(r'\bor \d+ \(\d+d\d+', first): tags.append('versatile_or_alt_damage')
            if re.search(r'Recharge', a['name']): tags.append('recharge')
            if re.search(r'Hit or Miss', d): tags.append('hit_or_miss')
            if re.search(r'Saving Throw', d): tags.append('also_saving_throw')
            if re.search(r'Grappled|Prone|Poisoned|Restrained|Stunned|Paralyzed|Blinded|Frightened|Charmed|Invisible|Incapacitated|Deafened', hit): tags.append('rider_condition')
            if not dmgs and re.search(r'Hit: \d+ damage|Hit: \d+ [A-Z][a-z]+ damage|Hit: \d+ \(', hit): tags.append('flat_or_odd_damage')
            if not m and not tags: tags.append('other_header_shape')
            if re.search(r'^Melee or Ranged', d): tags.append('melee_or_ranged')
            if re.search(r'Attack Roll: \+\d+, reach \d+ ft\. or range', d): tags.append('reach_or_range')
            for t in tags:
                mon_stats['tag:' + t] += 1
                if len(mon_ex[t]) < 4: mon_ex[t].append(nm + ' -- ' + ' '.join(d.split()[:20]))
            irregular = [t for t in tags if t not in ('rider_condition', 'also_saving_throw', 'melee_or_ranged', 'reach_or_range')]
            if not irregular and m:
                mon_stats['clean_regular'] += 1
            # flag each distinct irregular tag for the sampler
            for t in irregular:
                class MM: pass
                mm = re.search(r'Hit:', d)
                flag('monster.attack_irregular', nm, 'monster-action', d, mm or re.search(r'.', d), sub=t)
            if not irregular:
                mm = re.search(r'Hit:', d)
                # unflagged: only keep a small clean sample later
mon_summary = dict(mon_stats)
mon_summary['actions_scanned'] = mon_actions_total
mon_summary['attack_prose_with_structured_damage_and_attack_bonus_fields'] = structured_ok
summary['monsters'] = mon_summary
summary['monster_examples'] = mon_ex

# ------------------------------------------------------------------ spell use coverage summary
if PACK_USE is not None:
    summary['spell_use'] = {'with_use': sum(1 for v in PACK_USE.values() if v), 'without_use': sum(1 for v in PACK_USE.values() if not v), 'total': len(PACK_USE)}

# ------------------------------------------------------------------ totals
table = collections.OrderedDict()
for (cat, sub), ents in sorted(counts.items(), key=lambda kv: (kv[0][0], str(kv[0][1]))):
    key = cat + (f' [{sub}]' if sub else '')
    table[key] = len(ents)
summary['counts'] = table
# counts per entry type for features
by_type = collections.defaultdict(lambda: collections.defaultdict(int))
for (cat, sub), ents in counts.items():
    for (n, t) in ents:
        by_type[cat + (f' [{sub}]' if sub else '')][t.split(':')[0]] += 1
grant_cats = [c for c in {k[0] for k in counts} if c.startswith('feat.') and not c.startswith(('feat.option_menu', 'feat.reactive', 'feat.spend_points.pool', 'feat.proficiency.kind', 'feat.gear'))]
summary['distinct_feature_entries_flagged_any_grant_category'] = len({e for k, ents in counts.items() if k[0] in grant_cats for e in ents})
summary['feature_corpus_size'] = len(corpus)
summary['counts_by_entry_type'] = {k: dict(v) for k, v in by_type.items()}

with open(os.path.join(args.out, 'flagged.jsonl'), 'w') as fh:
    for r in flagged:
        fh.write(json.dumps(r, ensure_ascii=False) + '\n')
json.dump(summary, open(os.path.join(args.out, 'summary.json'), 'w'), indent=1, ensure_ascii=False)
for k, v in table.items():
    print(f'{v:5d}  {k}')
print(len(flagged), 'flagged records')
