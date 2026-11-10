// Self-test for the AGT-2715 lane-string gate: proves it fails on a raw lane
// key interpolation (AGT-3591, the global-search result badge) instead of
// passing vacuously, and that the laneName() form passes.
//
// Run: node --test scripts/check-lane-strings.test.mjs (check-lane-strings.mjs
// itself re-checks the rule on every lint:structure run)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const CHECKER = fileURLToPath(new URL('./check-lane-strings.mjs', import.meta.url));
const PRESENTATION = fileURLToPath(new URL('../src/app/models/lane-presentation.ts', import.meta.url));
const TEMPLATE = 'src/app/features/studio-shell/components/global-search/global-search.component.html';

function run(html) {
  const dir = mkdtempSync(join(tmpdir(), 'lane-strings-'));
  try {
    const module = join(dir, 'src/app/models/lane-presentation.ts');
    mkdirSync(dirname(module), { recursive: true });
    copyFileSync(PRESENTATION, module);
    const template = join(dir, TEMPLATE);
    mkdirSync(dirname(template), { recursive: true });
    writeFileSync(template, html, 'utf8');
    // The checker lists files through git, so the fixture must be a repository.
    execFileSync('git', ['init', '-q'], { cwd: dir });
    try {
      const stdout = execFileSync(process.execPath, [CHECKER],
        { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
      return { code: 0, out: stdout };
    } catch (err) {
      return { code: err.status, out: `${err.stdout ?? ''}${err.stderr ?? ''}` };
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

test('fails on a raw lane key in the global-search badge', () => {
  const result = run('@if (item.lane) { <span class="search-badge">{{ item.lane }}</span> }\n');
  assert.equal(result.code, 1, result.out);
  assert.match(result.out, new RegExp(`${TEMPLATE}:1`));
  assert.match(result.out, /raw lane key "\{\{ item\.lane \}\}"/);
});

test('fails on a bare and an optional-chained lane key', () => {
  assert.equal(run('<span>{{ lane }}</span>\n').code, 1);
  assert.equal(run('<span>{{ row?.lane }}</span>\n').code, 1);
});

test('passes when the badge reads laneName() and laneTone()', () => {
  const result = run(
    '<span [attr.data-lane-tone]="laneTone(item.lane)">{{ laneName(item.lane) }}</span>\n'
    + '<span>{{ lane.label }}</span>\n',
  );
  assert.equal(result.code, 0, result.out);
});
