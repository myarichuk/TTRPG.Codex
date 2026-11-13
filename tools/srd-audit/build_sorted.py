import json, collections, re
import os; BASE = os.path.join(os.environ.get('SRD_AUDIT_DIR', '.'), '')
THEMES = {
 'PC': 'player choice', 'SG': 'state-gated (active Rage/other state)', 'CX': 'conditional extra dice on hit',
 'DT': 'damage type chosen per cast', 'SM': 'summon or stat block', 'TG': 'target not in planned shape',
 'RC': 'recurring per-turn effect', 'SET': 'sets value instead of adding', 'TB': 'table or rolled outcome',
 'MM': 'several modes or dice in one spell', 'HL': 'heal shape not expressible', 'CC': 'compound condition',
 'PT': 'per-turn choice, not build-time', 'CI': 'condition immunity not in shape', 'OT': 'other',
}
recs = [json.loads(l) for l in open(BASE + 'flagged.jsonl', encoding='utf-8') if l.strip()]
sel = [r for r in recs if (r['category'].startswith('feat.') or r['category'].startswith('spell.'))
       and r['category'] != 'spell.no_use_bucket:no_dice_no_number' and not r['category'].startswith('ref.')]
lab = {}
for line in open(BASE + 'labels.txt', encoding='utf-8'):
    line = line.rstrip('\n')
    if not line: continue
    i, label, reason = line.split('|', 2)
    assert int(i) not in lab
    assert label in ('fits', 'breaks', 'false_positive', 'unsure'), line
    lab[int(i)] = (label, reason)
assert sorted(lab) == list(range(len(sel))), (len(sel), len(lab))
out = []
for i, r in enumerate(sel):
    label, reason = lab[i]
    theme = ''
    if label == 'breaks':
        assert ':' in reason, reason
        theme, reason = reason.split(':', 1)
        theme = theme.strip(); reason = reason.strip()
    rr = dict(r); rr['label'] = label; rr['reason'] = reason; rr['theme'] = THEMES.get(theme, theme) if theme else ''; rr['idx'] = i
    out.append(rr)
with open(BASE + 'sorted.jsonl', 'w', encoding='utf-8') as f:
    for rr in out: f.write(json.dumps(rr, ensure_ascii=False) + '\n')

cats = collections.OrderedDict()
for rr in out:
    c = cats.setdefault(rr['category'], collections.Counter())
    c[rr['label']] += 1
L = ['fits', 'breaks', 'false_positive', 'unsure']
md = ['# Sorting summary (feat.* and spell.*, 288 records)', '',
      'Excluded: spell.no_use_bucket:no_dice_no_number (133) and ref.*. Each of the 288 records was labelled by reading its text, not by regex.', '',
      '## Counts by category', '', '| category | fits | breaks | false_positive | unsure | total |', '|---|---|---|---|---|---|']
tot = collections.Counter()
for c, cnt in cats.items():
    md.append('| %s | %d | %d | %d | %d | %d |' % (c, cnt['fits'], cnt['breaks'], cnt['false_positive'], cnt['unsure'], sum(cnt.values())))
    tot.update(cnt)
md.append('| **TOTAL** | %d | %d | %d | %d | %d |' % (tot['fits'], tot['breaks'], tot['false_positive'], tot['unsure'], sum(tot.values())))
md += ['', '## Breaks reasons per category (grouped by theme)', '']
bycat = collections.defaultdict(lambda: collections.defaultdict(list))
for rr in out:
    if rr['label'] == 'breaks':
        bycat[rr['category']][rr['theme']].append(rr['entry'])
for c in cats:
    if c not in bycat: continue
    md.append('**%s**' % c)
    for th, ents in sorted(bycat[c].items(), key=lambda kv: -len(kv[1])):
        ex = ', '.join(dict.fromkeys(ents).__iter__().__class__ and list(dict.fromkeys(ents))[:3])
        md.append('- %s (%d): e.g. %s' % (th, len(ents), ex))
    md.append('')
md += ['## Top 5 breaks reasons overall', '']
allth = collections.Counter(); allex = collections.defaultdict(list)
for rr in out:
    if rr['label'] == 'breaks':
        th = rr['theme']; allth[th] += 1; allex[th].append(rr['entry'])
for n, (th, k) in enumerate(allth.most_common(5), 1):
    ex = ', '.join(list(dict.fromkeys(allex[th]))[:4])
    md.append('%d. %s: %d records, e.g. %s' % (n, th, k, ex))
md += ['', '## Unsure (every record)', '']
for rr in out:
    if rr['label'] == 'unsure':
        md.append('- [%d] %s | %s | %s' % (rr['idx'], rr['category'], rr['entry'], rr['reason']))
md += ['', '## Not read', '', 'None of the 288 was skipped. Full text was checked for ambiguous feature and feat rows (features, traits, feats, subclasses) and for Conjure Celestial in the spell source. Other spell snippets were judged as given.', '', '## Assumptions', '', '- Count-based spell fits assume the shape can scale a count per slot level or cantrip level: the 23 slot-level target and bolt/dart/ray rows, plus Eldritch Blast beams (29, 30).', '- Shillelagh (73) and True Strike (83) assume a spell use can carry atLevels dice scaling.']
open(BASE + 'sorted_summary.md', 'w', encoding='utf-8').write('\n'.join(md) + '\n')
print('records', len(out), dict(tot))
print('\n'.join(md[:40]))
