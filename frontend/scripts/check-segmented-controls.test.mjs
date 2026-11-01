import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const checker = fileURLToPath(new URL('./check-segmented-controls.mjs', import.meta.url));
function run(html) {
  const dir = mkdtempSync(join(tmpdir(), 'segmented-gate-'));
  try {
    mkdirSync(join(dir, 'src'), { recursive: true });
    writeFileSync(join(dir, 'src', 'new.html'), html);
    writeFileSync(join(dir, 'baseline.json'), JSON.stringify({ documented: {} }));
    try {
      return { code: 0, out: execFileSync(process.execPath, [checker, '--baseline', 'baseline.json'], { cwd: dir, encoding: 'utf8' }) };
    } catch (error) {
      return { code: error.status, out: String(error.stderr) };
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
test('accepts the shared control', () => {
  assert.equal(run('<app-segmented-control [options]="options" [value]="selected()" />').code, 0);
});
test('rejects a new hand-built pressed button', () => {
  const result = run('<button [attr.aria-pressed]="selected() === option">Option</button>');
  assert.equal(result.code, 1);
  assert.match(result.out, /hand-built selection button/);
});
test('rejects a new hand-built active-class button', () => {
  assert.equal(run('<button [class.choice--active]="selected() === option">Option</button>').code, 1);
});
test('rejects a new hand-built radio button', () => {
  assert.equal(run('<button [attr.aria-checked]="selected() === option">Option</button>').code, 1);
});
test('allows an independent switch', () => {
  assert.equal(run('<button role="switch" [attr.aria-checked]="on()">On</button>').code, 0);
});
