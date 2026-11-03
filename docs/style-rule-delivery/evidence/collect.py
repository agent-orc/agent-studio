import json, os, re, glob, sys
ROOT = os.path.expanduser('~/.local/state/agent-studio/clean-context')
WT = re.compile(r'/home/agent/runner-work/(PROJ-[A-Z0-9]+)/worktrees/([A-Z]+-\d+)')
out = []
def first_text(c):
    if isinstance(c, str): return c
    if isinstance(c, list):
        return '\n'.join(x.get('text','') for x in c if isinstance(x, dict) and x.get('type') in ('text','input_text'))
    return ''
# Claude
for f in glob.glob(ROOT + '/claude/*/projects/*/*.jsonl'):
    cwd=None; prompt=None; ts=None; calls=[]; edits=[]
    try:
        for line in open(f, errors='replace'):
            try: d=json.loads(line)
            except: continue
            if cwd is None and d.get('cwd'): cwd=d['cwd']
            if ts is None and d.get('timestamp'): ts=d['timestamp']
            m=d.get('message') or {}
            if d.get('type')=='user' and prompt is None:
                t=first_text(m.get('content'))
                if t and len(t)>200: prompt=t
            if d.get('type')=='assistant' and isinstance(m.get('content'), list):
                for c in m['content']:
                    if c.get('type')=='tool_use':
                        inp=c.get('input',{}); name=c.get('name')
                        s=json.dumps(inp)
                        calls.append(name+' '+s[:4000])
                        if name in ('Edit','Write','MultiEdit','NotebookEdit') and inp.get('file_path'):
                            edits.append(inp['file_path'])
    except Exception as e:
        continue
    if not cwd: continue
    mm=WT.search(cwd)
    if not mm or not prompt: continue
    out.append(dict(cli='claude', project=mm.group(1), key=mm.group(2), ts=ts, prompt=prompt, calls=calls, edits=edits, file=f))
# Codex
PATCH=re.compile(r'\*\*\* (?:Add|Update|Delete) File: ([^\s\\"]+)')
for f in glob.glob(ROOT + '/codex/*/sessions/**/*.jsonl', recursive=True):
    cwd=None; prompt=None; ts=None; calls=[]; edits=[]
    try:
        for line in open(f, errors='replace'):
            try: d=json.loads(line)
            except: continue
            p=d.get('payload') or {}
            if d.get('type')=='session_meta':
                cwd=p.get('cwd'); ts=p.get('timestamp') or d.get('timestamp')
            if d.get('type')=='response_item':
                if p.get('type')=='message' and p.get('role')=='user' and prompt is None:
                    t=first_text(p.get('content'))
                    if t and not t.lstrip().startswith('<') and len(t)>200 and not t.startswith('# AGENTS.md'):
                        prompt=t
                if p.get('type') in ('custom_tool_call','function_call'):
                    s=p.get('input') or p.get('arguments') or ''
                    if not isinstance(s,str): s=json.dumps(s)
                    calls.append((p.get('name') or '')+' '+s[:4000])
                    edits += PATCH.findall(s)
    except Exception:
        continue
    if not cwd: continue
    mm=WT.search(cwd)
    if not mm or not prompt: continue
    out.append(dict(cli='codex', project=mm.group(1), key=mm.group(2), ts=ts, prompt=prompt, calls=calls, edits=edits, file=f))
json.dump(out, open('/tmp/agt3465/sessions.json','w'))
from collections import Counter
print(len(out), Counter(o['cli'] for o in out), len(set(o['key'] for o in out)))
print(Counter(o['project'] for o in out).most_common(12))
print(min(o['ts'] for o in out), max(o['ts'] for o in out))
