import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const script = fileURLToPath(new URL('./check-explicit-locales.mjs', import.meta.url));

test('product source contains no locale-less formatter calls', () => {
  const result = spawnSync(process.execPath, [script], { encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0, result.stderr);
});

test('the guard rejects missing locales and accepts explicit locales', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'explicit-locales-'));
  try {
    const source = path.join(dir, 'sample.ts');
    fs.writeFileSync(source, 'export const formatted = (1000).toLocaleString();\n');
    const bad = spawnSync(process.execPath, [script, dir], { encoding: 'utf8', windowsHide: true });
    assert.equal(bad.status, 1);
    assert.match(bad.stderr, /sample\.ts:1: pass an explicit locale/);

    fs.writeFileSync(source, "export const formatted = (1000).toLocaleString('en-US');\n");
    const good = spawnSync(process.execPath, [script, dir], { encoding: 'utf8', windowsHide: true });
    assert.equal(good.status, 0, good.stderr);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
