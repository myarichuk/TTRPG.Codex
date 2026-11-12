import os; BASE = os.path.join(os.environ.get('SRD_AUDIT_DIR', '.'), '')
lines = [l.rstrip('\n') for l in open(BASE + 'labels.txt', encoding='utf-8') if l.strip()]
lab = {}
for l in lines:
    i, label, reason = l.split('|', 2)
    lab[int(i)] = [label, reason]
over = {
 20: ('breaks', 'MM: per-creature choice of heal or damage light'),
 96: ('fits', 'single armor tag test satisfies the when clause'),
 100: ('fits', 'single heavy-armor tag test satisfies the when clause'),
 196: ('fits', 'single heavy-armor tag test satisfies the when clause'),
 235: ('fits', 'single armor tag test satisfies the when clause'),
 275: ('fits', 'single armor tag test satisfies the when clause'),
 103: ('false_positive', 'rage-ending rule, not a gated benefit'),
 151: ('breaks', 'CC: compound condition, not armor and not shield'),
 124: ('fits', 'reactive: counter and text suffice'),
 134: ('fits', 'reactive: counter and text suffice'),
 226: ('fits', 'reactive: counter and text suffice'),
 270: ('fits', 'reactive: counter and text suffice'),
 102: ('fits', 'reactive: counter and text suffice'),
 218: ('false_positive', 'spell-damage rider, not a hit trigger'),
 250: ('fits', 'fixed poison resistance; the resistance part fits'),
 26: ('false_positive', 'single beam; no count of beams'),
 60: ('false_positive', 'single beam; no count of beams'),
 221: ('false_positive', 'once-free-cast text, not at-will'),
 238: ('false_positive', 'once-free-cast text, not at-will'),
 148: ('false_positive', 'death save crit range, not an attack crit range'),
 93: ('false_positive', 'rule sentence about extending Rage, not a menu'),
 98: ('false_positive', 'rule sentence about skill checks, not a menu'),
 212: ('false_positive', 'rule sentence on Daze effect, not a menu'),
 0: ('fits', 'second delayed roll of same type; count plus second roll covers'),
 86: ('fits', 'second delayed roll of same type; count plus second roll covers'),
 84: ('fits', 'second damage roll covered by second roll'),
 87: ('fits', 'second damage roll covered by second roll'),
 89: ('fits', 'second damage roll covered by second roll'),
 286: ('false_positive', 'rule sentence using Reaction, not a reaction feature'),
}
for i, (label, reason) in over.items():
    lab[i] = [label, reason]
# exact literal reason for proficiency and expertise player-choice records
for i in [97, 114, 176, 193, 206, 228, 268, 271, 112, 194, 197, 204, 229, 262]:
    assert lab[i][0] == 'breaks', (i, lab[i])
    lab[i] = ['breaks', 'PC: player choice']
with open(BASE + 'labels.txt', 'w', encoding='utf-8') as f:
    for i in sorted(lab):
        f.write('%d|%s|%s\n' % (i, lab[i][0], lab[i][1]))
print('patched', len(lab))
