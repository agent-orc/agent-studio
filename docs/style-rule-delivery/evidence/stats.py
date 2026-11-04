import json
from collections import Counter
R=json.load(open('/tmp/agt3465/rows.json'))
N=len(R)
print('N',N)
# area firing rate
fire=Counter(); wordfire=Counter()
for r in R:
    for a in r['areas']: fire[a]+=1
    for a,h in r['wordhits'].items():
        if h: wordfire[a]+=1
for a in ['backend','runner','frontend','git','filesystem','refactor','delegation']:
    print(f"{a:11s} substring {fire[a]:3d} ({fire[a]/N:.0%})  wordboundary {wordfire[a]:3d}")
# needle contributions: card where area fired only via needle n
solo=Counter(); anyn=Counter()
for r in R:
    for a,hits in r['areas'].items():
        for n in hits: anyn[(a,n)]+=1
        if len(hits)==1: solo[(a,hits[0])]+=1
print('top needles', anyn.most_common(25))
print('sole-trigger needles', solo.most_common(15))
# ground truth: cards with surface known (files>0)
G=[r for r in R if r['nfiles']>0]
print('cards with delivered files',len(G))
for a in ['frontend','backend']:
    tp=sum(1 for r in G if a in r['areas'] and a in r['surf'])
    fp=sum(1 for r in G if a in r['areas'] and a not in r['surf'])
    fn=sum(1 for r in G if a not in r['areas'] and a in r['surf'])
    wtp=sum(1 for r in G if r['wordhits'][a] and a in r['surf'])
    wfp=sum(1 for r in G if r['wordhits'][a] and a not in r['surf'])
    wfn=sum(1 for r in G if not r['wordhits'][a] and a in r['surf'])
    print(a,'substring tp fp fn',tp,fp,fn,'precision %.0f%% recall %.0f%%'%(100*tp/max(1,tp+fp),100*tp/max(1,tp+fn)),
          '| word tp fp fn',wtp,wfp,wfn,'precision %.0f%% recall %.0f%%'%(100*wtp/max(1,wtp+wfp),100*wtp/max(1,wtp+wfn)))
print('surfaces', Counter(tuple(r['surf']) for r in G).most_common())
# blocks
bc=Counter(b for r in R for b in r['blocks'])
print('blocks',bc.most_common())
print('blocks per enriched card',Counter(len(r['blocks']) for r in R if r['enriched']))
# style guide reads among frontend-surface cards
F=[r for r in G if 'frontend' in r['surf']]
print('frontend cards',len(F), Counter(r['project'] for r in F))
for g in ['angular-components.md','frontend-styling.md','style-guide-hard-rules.md','living-rules.md','frontend/AGENTS.md','design-principles.md','docs/quality/README.md','admin-design-guideline']:
    print(' read',g,sum(r['reads'][g] for r in F))
print(' any guide read',sum(any(r['reads'][g] for g in ['angular-components.md','frontend-styling.md','style-guide-hard-rules.md','living-rules.md','design-principles.md']) for r in F))
FA=[r for r in F if any(b.startswith('style-guide:') for b in r['blocks'])]
print('frontend cards with style-guide block',len(FA),'of which read cited guide', sum(any(r['reads'][b.split(':')[1]+'.md'] for b in r['blocks'] if b.startswith('style-guide:')) for r in FA))
FT=[r for r in F if 'frontend-design-tokens-components' in r['blocks']]
print('frontend cards with tokens block',len(FT),'read frontend/AGENTS.md',sum(r['reads']['frontend/AGENTS.md'] for r in FT),'read design-principles',sum(r['reads']['design-principles.md'] for r in FT))
# token cost
import statistics
add=[ (r['launch_len']-r['authored_len'])/4 for r in R if r['enriched']]
print('appended tokens median %.0f p90 %.0f max %.0f'%(statistics.median(add),sorted(add)[int(.9*len(add))],max(add)))
print('header prompt cards',sum(r['header'] for r in R),'header files touched',sum(1 for r in R if r['headerfiles']))
for r in R:
    if r['headerfiles']: print('  ',r['key'],r['last'][:10],r['headerfiles'][:4],r['blocks'])
