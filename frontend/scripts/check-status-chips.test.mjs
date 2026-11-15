import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const checker = fileURLToPath(new URL('./check-status-chips.mjs', import.meta.url));
function run(html, documented = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'status-chip-gate-'));
  try {
    mkdirSync(join(dir, 'src'), { recursive: true });
    writeFileSync(join(dir, 'src', 'new.html'), html);
    writeFileSync(join(dir, 'baseline.json'), JSON.stringify({ documented }));
    try {
      return { code: 0, out: execFileSync(process.execPath, [checker, '--baseline', 'baseline.json'], { cwd: dir, encoding: 'utf8' }) };
    } catch (error) {
      return { code: error.status, out: String(error.stderr) };
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}
test('accepts the shared indicator', () => {
  assert.equal(run('<app-status-indicator tone="success" label="Connected" [compact]="true" />').code, 0);
});
test('rejects a pill toned by a data attribute', () => {
  const result = run('<span class="deploy__pill" [attr.data-tone]="tone()">{{ label() }}</span>');
  assert.equal(result.code, 1);
  assert.match(result.out, /hand-built status chip/);
});
test('rejects a badge toned by a modifier class binding', () => {
  assert.equal(run('<span class="run-badge" [class.run-badge--failed]="failed()">Failed</span>').code, 1);
});
test('rejects a dot toned by a static modifier or a state attribute', () => {
  assert.equal(run('<i class="host__dot host__dot--offline"></i>').code, 1);
  assert.equal(run('<span class="check-dot" [attr.data-state]="check.state"></span>').code, 1);
});
test('rejects a chip with a computed tone class', () => {
  assert.equal(run('<span class="step-chip" [class]="\'step-chip--\' + step.status">{{ step.status }}</span>').code, 1);
});
test('allows untoned tags, selected filters and plain status text', () => {
  assert.equal(run('<span class="tag-chip">{{ tag }}</span>').code, 0);
  assert.equal(run('<button class="filter-chip" [class.filter-chip--active]="on()">All</button>').code, 0);
  assert.equal(run('<td class="row__status" [attr.data-status]="row.status">{{ row.status }}</td>').code, 0);
});
test('a documented exception holds only for its pinned signature', () => {
  const pinned = run('<span class="lane-pill" [attr.data-tone]="laneTone()">Review</span>');
  const signatures = JSON.parse(pinned.out.match(/Signatures now: (\[.*\])/)[1]);
  const documented = { 'src/new.html': { reason: 'Lane pill uses the lane tone (R9), not a status tone.', signatures } };
  assert.equal(run('<span class="lane-pill" [attr.data-tone]="laneTone()">Review</span>', documented).code, 0);
  assert.equal(run('<span class="lane-pill" [attr.data-tone]="laneTone()">Review</span><span class="x-dot" data-tone="warn"></span>', documented).code, 1);
});
test('an exception needs a written reason', () => {
  const result = run('<span>Plain</span>', { 'src/new.html': { reason: 'short', signatures: [] } });
  assert.equal(result.code, 1);
  assert.match(result.out, /written reason/);
});
