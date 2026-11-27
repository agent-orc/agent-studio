import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const prepare = fileURLToPath(new URL('../.agent-studio/prepare-integration.sh', import.meta.url));

test('warm frontend preparation reinstalls only for changed inputs or a missing installation', () => {
  const root = mkdtempSync(path.join(tmpdir(), 'integration-prepare-'));
  try {
    mkdirSync(path.join(root, 'frontend'));
    mkdirSync(path.join(root, 'scripts'));
    writeFileSync(path.join(root, 'frontend/package.json'), '{}');
    writeFileSync(path.join(root, 'frontend/package-lock.json'), '{}');
    writeFileSync(path.join(root, '.nvmrc'), '22');
    writeFileSync(path.join(root, 'scripts/npm-frontend.sh'), `#!/bin/sh
set -eu
if [ "$1" = --version ]; then echo 11; exit; fi
test "$1" = ci
mkdir -p frontend/node_modules
touch frontend/node_modules/.package-lock.json
echo installed >> installs
`);
    const run = () => {
      const result = spawnSync('bash', [prepare, 'frontend'], { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 30000 });
      assert.equal(result.status, 0, result.stderr);
      return readFileSync(path.join(root, 'installs'), 'utf8').trim().split('\n').length;
    };
    assert.equal(run(), 1);
    assert.equal(run(), 1);
    writeFileSync(path.join(root, 'frontend/package-lock.json'), '{"changed":true}');
    assert.equal(run(), 2);
    rmSync(path.join(root, 'frontend/node_modules/.package-lock.json'));
    assert.equal(run(), 3);
    writeFileSync(path.join(root, '.nvmrc'), '24');
    assert.equal(run(), 4);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
