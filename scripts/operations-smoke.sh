#!/usr/bin/env bash
# Private, disposable Operations boundary rehearsal. Never targets a product stack.
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_project="operations-smoke-$$"
compose=(docker compose -p "$compose_project" -f "$repo_root/deploy/compose/operations/compose.yaml")
cleanup() { "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1; }
trap cleanup EXIT
"${compose[@]}" up --build -d
# Pull before attaching the probe to the deliberately egress-free network.
docker image inspect python:3.12-alpine >/dev/null 2>&1 || docker pull python:3.12-alpine
probe() {
  docker run --rm -i --user 1654:1654 --network "${compose_project}_operations" \
    --mount "type=volume,source=${compose_project}_client-secret,target=/run/client,readonly" \
    --mount "type=volume,source=${compose_project}_probe,target=/evidence" \
    python:3.12-alpine python - "$@"
}
# Store public receipt only in a scratch volume, owned by the non-root probe.
docker run --rm --network none -v "${compose_project}_probe:/evidence" python:3.12-alpine \
  sh -c 'chown 1654:1654 /evidence'
trap 'cleanup; docker volume rm "${compose_project}_probe" >/dev/null 2>&1' EXIT
probe <<'PY'
import hashlib, json, time, urllib.request, urllib.error
from datetime import datetime, timedelta, timezone
from pathlib import Path
base='http://operations-server:5081/api/operations/v1/'
headers={'Authorization':'Bearer '+Path('/run/client/token').read_text().strip(), 'X-Operations-Protocol':'1', 'Content-Type':'application/json'}
def request(path, body=None, extra=None):
    req=urllib.request.Request(base+path, data=None if body is None else json.dumps(body).encode(), headers=headers | (extra or {}))
    with urllib.request.urlopen(req, timeout=5) as response:
        return json.load(response)
command={'idempotencyKey':'smoke', 'actor':'smoke', 'agentId':'container-agent', 'operationId':'host.inspect', 'version':1, 'input':{}, 'inputDigest':hashlib.sha256(b'{}').hexdigest(), 'deadline':(datetime.now(timezone.utc)+timedelta(minutes=3)).isoformat(), 'correlationId':'operations-smoke'}
for attempt in range(40):
    try:
        receipt=request('commands', command)
        break
    except (urllib.error.URLError, TimeoutError):
        if attempt == 39: raise
        time.sleep(1)
for attempt in range(40):
    receipt=request('attempts/'+receipt['id'])
    if receipt['state']=='succeeded': break
    time.sleep(1)
assert receipt['state']=='succeeded', receipt['state']
assert receipt['result']['output']['processors'] > 0
assert receipt['result']['cleanupConfirmed']
for header in [{'Origin':'https://foreign.example'}, {'Cookie':'session=foreign'}, {'Authorization':'Bearer invalid'}]:
    try:
        request('catalogue', extra=header)
        raise AssertionError('denial was not enforced')
    except urllib.error.HTTPError as error:
        assert error.code == 401, error.code
assert request('commands', command)['id'] == receipt['id']
Path('/evidence/receipt.json').write_text(json.dumps({'command':command,'id':receipt['id'],'resultDigest':receipt['resultDigest']}))
print(json.dumps({'diagnostic':'passed','browserDenials':'passed','idempotency':'passed','attemptId':receipt['id']}))
PY
"${compose[@]}" restart operations-server
probe <<'PY'
import json, time, urllib.request, urllib.error
from pathlib import Path
receipt=json.loads(Path('/evidence/receipt.json').read_text())
headers={'Authorization':'Bearer '+Path('/run/client/token').read_text().strip(), 'X-Operations-Protocol':'1','Content-Type':'application/json'}
for attempt in range(40):
    try:
        req=urllib.request.Request('http://operations-server:5081/api/operations/v1/commands', data=json.dumps(receipt['command']).encode(), headers=headers)
        with urllib.request.urlopen(req, timeout=5) as response: restored=json.load(response)
        break
    except (urllib.error.URLError, TimeoutError):
        if attempt == 39: raise
        time.sleep(1)
assert restored['id']==receipt['id'] and restored['resultDigest']==receipt['resultDigest']
print(json.dumps({'serverRestartReplay':'passed','attemptId':restored['id']}))
PY
