// Self-test for the ADM-17 disclosure gate: proves the checker actually fails
// on the three shapes it exists to catch, instead of passing vacuously.
//
// Run: node --test scripts/check-disclosure-markers.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const CHECKER = new URL('./check-disclosure-markers.mjs', import.meta.url).pathname;

function run(templates, baseline) {
  const dir = mkdtempSync(join(tmpdir(), 'adm17-'));
  try {
    const root = join(dir, 'src');
    mkdirSync(root, { recursive: true });
    for (const [name, html] of Object.entries(templates)) {
      writeFileSync(join(root, name), html, 'utf8');
    }
    const baselinePath = join(dir, 'baseline.json');
    writeFileSync(baselinePath, JSON.stringify(baseline), 'utf8');
    try {
      const stdout = execFileSync(
        process.execPath,
        [CHECKER, '--root', root, '--baseline', baselinePath],
        { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
      );
      return { code: 0, out: stdout };
    } catch (err) {
      return { code: err.status, out: `${err.stdout ?? ''}${err.stderr ?? ''}` };
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const COMPLIANT = `<button type="button" [attr.aria-expanded]="open()" (click)="toggle()">
  <app-disclosure-marker [open]="open()" />
  <span>Why this status?</span>
</button>`;

const BARE = `<button type="button" [attr.aria-expanded]="open()" (click)="toggle()">
  <span>Why this status?</span>
</button>`;

test('accepts a toggle that renders the shared marker', () => {
  const r = run({ 'ok.html': COMPLIANT }, { enforced: ['src/ok.html'], documented: {} });
  assert.equal(r.code, 0, r.out);
  assert.match(r.out, /Disclosure grammar OK/);
});

test('fails an enforced template whose toggle has no marker', () => {
  const r = run({ 'bare.html': BARE }, { enforced: ['src/bare.html'], documented: {} });
  assert.equal(r.code, 1);
  assert.match(r.out, /carries aria-expanded without the shared marker/);
});

test('fails a brand-new template that is in neither list', () => {
  const r = run({ 'new.html': BARE }, { enforced: [], documented: {} });
  assert.equal(r.code, 1);
  assert.match(r.out, /unclassified template/);
});

test('accepts a recorded divergence and rejects an empty reason', () => {
  const ok = run({ 'menu.html': BARE }, {
    enforced: [],
    documented: { 'src/menu.html': { kind: 'popup-trigger', reason: 'Overlay menu trigger, not an in-place body.' } },
  });
  assert.equal(ok.code, 0, ok.out);

  const bad = run({ 'menu.html': BARE }, {
    enforced: [],
    documented: { 'src/menu.html': { kind: 'popup-trigger', reason: 'because' } },
  });
  assert.equal(bad.code, 2);
  assert.match(bad.out, /needs a real reason/);
});

test('ignores aria-expanded mentioned in a template comment', () => {
  const r = run({ 'prose.html': '<!-- aria-expanded is the contract here -->\n<div>text</div>' }, {
    enforced: [],
    documented: {},
  });
  assert.equal(r.code, 0, r.out);
});

test('rejects a stale enforced entry for a template without any disclosure', () => {
  const r = run({ 'plain.html': '<div>nothing here</div>' }, { enforced: ['src/plain.html'], documented: {} });
  assert.equal(r.code, 1);
  assert.match(r.out, /stale entry/);
});
