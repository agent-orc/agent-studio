import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { test } from 'node:test';

test('prebuilt fingerprint changes for tracked edits and untracked files', () => {
  const root = mkdtempSync(join(tmpdir(), 'e2e-fingerprint-'));
  try {
    mkdirSync(join(root, 'scripts'));
    copyFileSync(resolve('scripts/e2e-source-fingerprint.sh'), join(root, 'scripts/e2e-source-fingerprint.sh'));
    writeFileSync(join(root, 'source.ts'), 'before\n');
    const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' });
    git('init', '-q');
    git('config', 'user.name', 'Fingerprint Test');
    git('config', 'user.email', 'fingerprint@example.invalid');
    git('add', '.');
    git('commit', '-qm', 'baseline');
    const fingerprint = () => execFileSync('bash', ['scripts/e2e-source-fingerprint.sh'], { cwd: root, encoding: 'utf8' }).trim();
    const baseline = fingerprint();
    writeFileSync(join(root, 'source.ts'), 'after\n');
    const edited = fingerprint();
    assert.notEqual(edited, baseline);
    writeFileSync(join(root, 'new.ts'), 'untracked\n');
    assert.equal(fingerprint(), edited, 'unrelated generated files do not invalidate a build');
    mkdirSync(join(root, 'frontend', 'src'), { recursive: true });
    writeFileSync(join(root, 'frontend', 'src', 'new.ts'), 'untracked source\n');
    assert.notEqual(fingerprint(), edited);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
