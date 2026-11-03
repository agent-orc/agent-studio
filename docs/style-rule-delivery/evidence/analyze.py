import json, re
from collections import Counter, defaultdict
S=json.load(open('/tmp/agt3465/sessions.json'))
DIFF=json.load(open('/tmp/agt3465/diff_files.json'))
AREAS=[
 ("backend", ["backend", ".cs", "c#", ".net", "dotnet", "api", "endpoint", "service", "controller"]),
 ("runner", ["runner", "pickup", "cli", "agent", "orchestrator", "intake", "pre-step", "pipeline", "watchdog", "sentinel"]),
 ("frontend", ["frontend", "angular", ".ts", ".scss", "css", "style", "styling", "component", "ui", "design", "token", "layout", "button", "lane", "card", "visual"]),
 ("git", ["git", "commit", "push", "merge", "branch", "worktree", "workspace artifact", "auto-commit", "pull request"]),
 ("filesystem", ["job folder", "task folder", "job.json", "task.json", "lane", "state", "move", "reorder", "archive", ".orchestrator", "workspace"]),
 ("refactor", ["refactor", "split", "extract", "rename", "namespace", "move file", "decompose"]),
 ("delegation", ["delegate", "delegation", "sub-agent", "subagent", "parallel agent", "spawn agent", "multi-agent"])]
MARKERS=['## Prompt enrichment','## Delivery and approval','## Concept mode','## Verification scope','## Continuous Session Note','## Follow-up for this run','Run context: result files','Orchestrator note:']
def authored(p):
    cut=[p.find(m) for m in MARKERS if p.find(m)>=0]
    return p[:min(cut)] if cut else p
def areas(text):
    t=text.lower(); res={}
    for a,ns in AREAS:
        hit=[n for n in ns if n in t]
        if hit: res[a]=hit
    return res
def word_hit(text,n):
    return re.search(r'(?<![a-z0-9])'+re.escape(n)+r'(?![a-z0-9])', text.lower()) is not None
# per card: latest session with enrichment, else latest
cards={}
for s in sorted(S,key=lambda s:s['ts'] or ''):
    c=cards.setdefault(s['key'],dict(key=s['key'],project=s['project'],sessions=[],calls=[],edits=[]))
    c['sessions'].append(s); c['calls']+=s['calls']; c['edits']+=s['edits']
    if '## Prompt enrichment' in s['prompt'] or 'prompt' not in c:
        c['prompt']=s['prompt']; c['ts']=s['ts']; c['cli']=s['cli']
    c['last']=s['ts']
def rel(p,key,proj):
    m=re.search(r'/worktrees/'+re.escape(key)+r'/(.*)',p)
    return m.group(1) if m else p
def surface(files):
    s=set()
    for f in files:
        fl=f.lower()
        if fl.startswith('/home/agent/runner-work/tasks') or '/results/' in fl: continue
        if fl.endswith(('.scss','.css')) or (fl.startswith('frontend/') and fl.endswith(('.ts','.html','.scss'))) or fl.endswith(('.tsx','.vue','.astro','.svelte')) or (fl.endswith('.html') and not fl.startswith('docs/')):
            s.add('frontend')
        if fl.endswith('.cs') or fl.startswith('backend/'): s.add('backend')
        if fl.startswith('docs/') or fl.endswith('.md'): s.add('docs')
    return s
rows=[]
for k,c in cards.items():
    a=authored(c['prompt'])
    ar=areas(a)
    blocks=re.findall(r'^- \*\*.*?\*\* \(`([^`]+)`\)', c['prompt'][c['prompt'].find('## Prompt enrichment'):] if '## Prompt enrichment' in c['prompt'] else '', re.M)
    files=[rel(e,k,c['project']) for e in c['edits']]
    if k in DIFF and c['project']=='PROJ-002': files=list(set(files)|set(DIFF[k]))
    sf=surface(files)
    concept=('Concept mode' in c['prompt'])
    calls='\n'.join(c['calls'])
    rows.append(dict(key=k,project=c['project'],cli=c['cli'],last=c['last'],areas=ar,blocks=blocks,surf=sorted(sf),nfiles=len(files),concept=concept,
        enriched='## Prompt enrichment' in c['prompt'], authored_len=len(a), launch_len=len(c['prompt']),
        reads={g: (g in calls) for g in ['angular-components.md','frontend-styling.md','style-guide-hard-rules.md','living-rules.md','frontend/AGENTS.md','dotnet-backend.md','design-principles.md','docs/quality/README.md','admin-design-guideline']},
        wordhits={a:[n for n in ns if word_hit(a_text,n)] for a,ns in AREAS for a_text in [a] } if False else {ar_name:[n for n in AREAS_d if word_hit(a,n)] for ar_name,AREAS_d in AREAS},
        header=bool(re.search(r'\bheader\b',a.lower())),
        headerfiles=[f for f in files if re.search(r'detail-header|header-quota|usage-cockpit-header|pane-header|app\.html|app\.scss|studio-shell',f)]))
json.dump(rows,open('/tmp/agt3465/rows.json','w'),indent=0)
print('cards',len(rows),'enriched',sum(r['enriched'] for r in rows),'concept',sum(r['concept'] for r in rows))
print('window',min(r['last'] for r in rows),max(r['last'] for r in rows))
print(Counter(r['project'] for r in rows))
