import json,collections,sys
rs=[json.loads(l) for l in open('flagged.jsonl')]
pref=sys.argv[1]; n=int(sys.argv[2]) if len(sys.argv)>2 else 40
by=collections.defaultdict(list)
for r in rs:
    if r['category'].startswith(pref): by[r['category']].append(r)
for c,l in sorted(by.items()):
    print('##',c,len(l))
    for r in l[:n]: print('  -',r['entry'][:45],'|',r['snippet'][:150],'|',r.get('note','')[:80])
