#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash -n "$repo_root/scripts/monolith-networked-backup-restore.test.sh"
node --check "$repo_root/scripts/monolith-networked-backup-browser-capture.cjs"
python3 -m py_compile "$repo_root/scripts/monolith-networked-backup-work-verify.py"
bash "$repo_root/scripts/monolith-networked-backup-restore.test.sh" --plan | python3 -c '
import json, sys
plan = json.load(sys.stdin)
assert plan["schema"] == "agent-studio.networked-recovery-plan/v1"
for check in ("scheduled-backup", "manual-backup", "two-project-checkouts", "empty-host-restore", "sealed-secret"):
    assert check in plan["checks"], check
assert "no-duplicate-work-records" in plan["checks"]
assert "node" not in plan["requires"]
assert "node" in plan["conditionalRequires"]["JOB_RESULTS_DIR"]
'
python3 - "$repo_root/scripts/monolith-networked-backup-work-verify.py" <<'PY'
import importlib.util, pathlib, tempfile, json, sys
spec = importlib.util.spec_from_file_location('work_check', sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
with tempfile.TemporaryDirectory() as temporary:
    root = pathlib.Path(temporary)
    authority = root / 'workspace/.metadata/attempt-authority.json'
    authority.parent.mkdir(parents=True)
    authority.write_text(json.dumps({'runAttempts': [{'attemptId': 'run-1', 'taskKey': 'card-a'}],
                                     'reviewAttempts': [{'attemptId': 'review-1', 'taskKey': 'card-b'}]}))
    batch = root / 'home/.local/share/agentstudio/batch-gates/batch-1'
    batch.mkdir(parents=True)
    (batch / 'manifest.json').write_text(json.dumps({'batchId': 'batch-1', 'members': [{'taskKey': 'card-b'}]}))
    (batch / 'publication.json').write_text('{}')
    before = module.inventory(root)
    assert module.verify(before, module.inventory(root), False)['publications'] == 1
    try:
        module.verify(before, module.inventory(root), True)
    except ValueError:
        pass
    else:
        raise AssertionError('running-work requirement accepted no active attempts')
    (batch / 'manifest.json').write_text(json.dumps({'batchId': 'batch-1', 'members': [{'taskKey': 'card-c'}]}))
    try:
        module.verify(before, module.inventory(root), False)
    except ValueError:
        pass
    else:
        raise AssertionError('changed batch subject was accepted')
    (batch / 'manifest.json').write_text(json.dumps({'batchId': 'batch-1', 'members': [{'taskKey': 'card-b'}]}))
    authority.write_text(json.dumps({'runAttempts': [{'attemptId': 'run-1', 'taskKey': 'card-a'}],
                                     'reviewAttempts': [{'attemptId': 'review-1', 'taskKey': 'card-b'},
                                                        {'attemptId': 'review-2', 'taskKey': 'card-b'}]}))
    try:
        module.verify(before, module.inventory(root), False)
    except ValueError:
        pass
    else:
        raise AssertionError('second review attempt was accepted')
    authority.write_text(json.dumps({'runAttempts': [{'attemptId': 'run-1', 'taskKey': 'card-a', 'lastFence': 2}],
                                     'reviewAttempts': [{'attemptId': 'review-1', 'taskKey': 'card-b'}]}))
    try:
        module.verify(before, module.inventory(root), False)
    except ValueError:
        pass
    else:
        raise AssertionError('second coding claim was accepted')
    authority.write_text(json.dumps({'runAttempts': [{'attemptId': 'run-1', 'taskKey': 'card-a'}],
                                     'reviewAttempts': [{'attemptId': 'review-1', 'taskKey': 'card-b'}]}))
    second = batch.parent / 'batch-2'
    second.mkdir()
    (second / 'manifest.json').write_text(json.dumps({'batchId': 'batch-2', 'members': [{'taskKey': 'card-b'}]}))
    (second / 'publication.json').write_text('{}')
    try:
        module.verify(before, module.inventory(root), False)
    except ValueError:
        pass
    else:
        raise AssertionError('second publication was accepted')
PY
