// Publication invariants for the deployment story Dossier (AGT-W63, source
// AGT-2906), moved to its canonical path by AGT-2951.
//
// The move rewrote dozens of relative links, so this proves every local link
// and anchor inside the Dossier still resolves, the incoming navigation points
// at the one canonical copy, and the North star entry sits where AGT-W14 §5
// prescribes it.
//
//   node --test scripts/deployment-story-publication.test.mjs

import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import test from 'node:test';

const root = resolve(import.meta.dirname, '..');
const canonicalDir = join(root, 'docs/operations/deployment-story');
const canonicalEntry = join(canonicalDir, 'index.html');
const read = (file) => readFileSync(file, 'utf8');

function htmlLinks(html) {
  return [...html.matchAll(/\s(?:href|src)="([^"]*)"/g)].map((m) => m[1].replaceAll('&amp;', '&'));
}

function markdownLinks(md) {
  return [...md.matchAll(/\]\(([^)\s]+)\)/g)].map((m) => m[1]);
}

// Only real `id` attributes and `<a name>` are link targets; `data-decision-id`
// or `<meta name>` must not satisfy an anchor.
function htmlAnchors(html) {
  return new Set([
    ...[...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]),
    ...[...html.matchAll(/<a\b[^>]*?\sname="([^"]+)"/g)].map((m) => m[1]),
  ]);
}

// GitHub heading slugs (with -1, -2 for repeats) plus inline HTML ids.
function markdownAnchors(md) {
  const anchors = htmlAnchors(md);
  const seen = new Map();
  const prose = md.replace(/^```[\s\S]*?^```/gm, '');
  for (const [, heading] of prose.matchAll(/^#{1,6}\s+(.+?)\s*#*\s*$/gm)) {
    const base = heading.toLowerCase().replace(/<[^>]+>/g, '').replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-');
    const count = seen.get(base) ?? 0;
    seen.set(base, count + 1);
    anchors.add(count ? `${base}-${count}` : base);
  }
  return anchors;
}

function anchorsOf(file) {
  if (file.endsWith('.html')) return htmlAnchors(read(file));
  if (file.endsWith('.md')) return markdownAnchors(read(file));
  return null;
}

// Resolves one local link from `fromFile`; returns a failure string or null.
function checkLink(fromFile, link) {
  // External URLs and root-absolute Studio routes such as /?task=AGT-2736 are
  // not repository files.
  if (/^(?:[a-z][a-z0-9+.-]*:|\/)/i.test(link)) return null;
  const hash = link.indexOf('#');
  const pathPart = (hash < 0 ? link : link.slice(0, hash)).split('?')[0];
  const fragment = hash < 0 ? '' : decodeURIComponent(link.slice(hash + 1));
  const target = pathPart ? resolve(dirname(fromFile), decodeURI(pathPart)) : fromFile;
  const where = `${relative(root, fromFile)} -> ${link}`;
  if (!existsSync(target)) return `${where}: missing target`;
  if (hash < 0) return null;
  const anchors = statSync(target).isFile() ? anchorsOf(target) : null;
  if (!anchors) return `${where}: anchor on a target whose anchors cannot be checked`;
  return anchors.has(fragment) ? null : `${where}: missing anchor #${fragment}`;
}

// Slices from `start` to `end`, failing loudly when a marker has moved.
function between(text, start, end) {
  const from = text.indexOf(start);
  assert.ok(from >= 0, `marker not found: ${start}`);
  const to = text.indexOf(end, from + start.length);
  assert.ok(to >= 0, `marker not found after ${start}: ${end}`);
  return text.slice(from, to);
}

function workbenchFiles(dir) {
  const found = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) found.push(...workbenchFiles(full));
    else if (entry.name === 'workbench.json') found.push(full);
  }
  return found;
}

test('the link checker rejects every anchor error it claims to catch', () => {
  const dir = mkdtempSync(join(tmpdir(), 'deployment-story-links-'));
  try {
    writeFileSync(join(dir, 'page.html'),
      '<meta name="viewport"><h2 id="map">Map</h2><article data-decision-id="D1"></article><a name="legacy"></a>');
    writeFileSync(join(dir, 'guide.md'),
      '# Guide\n\n## Backup and restore rehearsal\n\n## Backup and restore rehearsal\n\n```\n## Not a heading\n```\n\n[self](#guide)\n');
    const from = join(dir, 'guide.md');
    const ok = ['page.html#map', 'page.html#legacy', 'page.html', '#guide',
      'guide.md#backup-and-restore-rehearsal', 'guide.md#backup-and-restore-rehearsal-1',
      'page.html#%6Dap', 'https://example.org/#x', '/?task=AGT-2906'];
    for (const link of ok) assert.equal(checkLink(from, link), null, link);
    const bad = {
      'page.html#missing': 'missing anchor',
      'page.html#D1': 'missing anchor',
      'page.html#viewport': 'missing anchor',
      '#absent': 'missing anchor',
      'guide.md#not-a-heading': 'missing anchor',
      'guide.md#backup-and-restore-rehearsal-2': 'missing anchor',
      'absent.html#map': 'missing target',
      '.#map': 'cannot be checked',
    };
    for (const [link, reason] of Object.entries(bad)) {
      assert.match(checkLink(from, link) ?? 'accepted', new RegExp(reason), link);
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('every local link and anchor in the relocated Dossier resolves', () => {
  const links = htmlLinks(read(canonicalEntry));
  const failures = links.map((l) => checkLink(canonicalEntry, l)).filter(Boolean);
  assert.deepEqual(failures, []);
  // Guards against the matcher silently finding nothing after a markup change.
  assert.ok(links.length >= 40, `expected the Dossier's evidence links, found ${links.length}`);
});

test('the Dossier names the AGT-2905 bus Dossier by its published AGT-W65 key', () => {
  const bus = JSON.parse(read(join(root, 'docs/task-server-bus/workbench.json')));
  assert.equal(bus.key, 'AGT-W65');
  assert.deepEqual(bus.sourceTaskKeys, ['AGT-2905']);
  const html = read(canonicalEntry);
  assert.ok(html.includes('href="../../task-server-bus/index.html"'));
  // The pre-publication wording said the card was linked because no Dossier
  // URL existed yet; the links now point at AGT-W65.
  assert.doesNotMatch(html, /not-yet-published URL|its card is linked instead/);
  assert.match(html, /published as AGT-W65/);
});

test('the navigation record links resolve from the Dossier directory', () => {
  const file = join(canonicalDir, 'navigation-integration.md');
  const failures = markdownLinks(read(file)).map((l) => checkLink(file, l)).filter(Boolean);
  assert.deepEqual(failures, []);
});

test('exactly one Dossier carries AGT-W63 and its entrypoint exists', () => {
  const owners = workbenchFiles(join(root, 'docs'))
    .filter((f) => JSON.parse(read(f)).key === 'AGT-W63')
    .map((f) => relative(root, f).replaceAll('\\', '/'));
  assert.deepEqual(owners, ['docs/operations/deployment-story/workbench.json']);
  const descriptor = JSON.parse(read(join(canonicalDir, 'workbench.json')));
  assert.ok(existsSync(join(canonicalDir, descriptor.entrypoint)));
  assert.deepEqual(descriptor.sourceTaskKeys, ['AGT-2906']);
  assert.equal(existsSync(join(root, 'docs/deployment-story')), false, 'the pre-move copy must be gone');
});

test('incoming navigation points at the canonical Dossier', () => {
  const incoming = [
    'docs/operations/nordstern/index.html',
    'docs/operations/operations-server-backchannel/index.html',
    'docs/start/README.md',
    'docs/credentials-and-logins/index.html',
  ];
  for (const rel of incoming) {
    const file = join(root, rel);
    const text = read(file);
    const links = [...htmlLinks(text), ...markdownLinks(text)]
      .filter((l) => l.includes('deployment-story/'));
    assert.ok(links.length > 0, `${rel} has no deployment story link`);
    for (const link of links) {
      assert.equal(checkLink(file, link), null);
      assert.equal(resolve(dirname(file), link.split('#')[0]), canonicalEntry, `${rel} -> ${link}`);
    }
  }
});

test('the North star lists it once in §5, after AGT-W49 and before AGT-W51', () => {
  const html = read(join(root, 'docs/operations/nordstern/index.html'));
  const s5 = between(html, '<h2 id="s5"', '<h2 id="s6"');
  const cards = [...s5.matchAll(/<a class="card" href="([^"]+)"/g)].map((m) => m[1]);
  const at = cards.indexOf('../deployment-story/index.html');
  assert.ok(at > 0, 'deployment story card missing from §5');
  assert.equal(cards[at - 1], '../operations-server-backchannel/index.html');
  assert.equal(cards[at + 1], '../docker-ausfuehrungswelt-migration/index.html');
  assert.equal(html.split('deployment-story/index.html').length - 1, 1, 'link-only: one North star mention');
  const history = between(html, '<h2 id="s6"', '<section id="s7"');
  assert.equal(history.includes('deployment-story'), false, 'a current journey must not sit in §6 history');
});

test('the docs index has exactly one row for it', () => {
  const rows = read(join(root, 'docs/start/README.md')).split('\n')
    .filter((line) => line.startsWith('|') && line.includes('deployment-story/index.html'));
  assert.equal(rows.length, 1);
  assert.match(rows[0], /AGT-2906/);
});

test('AGT-W49 links the journey and the AGT-2905 bus Dossier and leaves D1 to D4 untouched', () => {
  const html = read(join(root, 'docs/operations/operations-server-backchannel/index.html'));
  const deployment = between(html, '<section id="deployment">', '</section>');
  assert.match(deployment, /href="\.\.\/deployment-story\/index\.html"/);
  assert.match(deployment, /href="\.\.\/\.\.\/task-server-bus\/index\.html"/);
  assert.match(deployment, /one-box special case and an ordinary runner host/);
  assert.match(deployment, /quality work is made of shared pipeline-library steps/);
  // The reconciliation defers to W49's own decision record instead of
  // restating a status that later implementation slices may change.
  assert.match(deployment, /does not change the recorded status of D1 to D4/);
  assert.match(html, /<b>Status<\/b>\s*Decision pending/);
  for (const id of ['d1-architecture', 'd2-standard-deployment', 'd3-agt-2736', 'd4-orchestrator-sessions']) {
    assert.match(html, new RegExp(`data-decision-id="${id}"`));
  }
  // The develop-side D12 execution-role amendment survives the merge.
  assert.match(html, /D12 execution-role amendment \(2026-09-26\)/);
  assert.match(html, />Workstation host adapter</);
  assert.equal(/^(?:<{7}|={7}|>{7})/m.test(html), false, 'no merge markers');
});

test('the Dossier keeps the develop-side AGT-2942 log after the 1 October merge', () => {
  const html = read(canonicalEntry);
  assert.equal(/^(?:<{7}|={7}|>{7})/m.test(html), false, 'no merge markers');
  const log = between(html, '<!-- agent-studio:implementation-log:start -->', '<!-- agent-studio:implementation-log:end -->');
  // AGT-2942 edited the pre-move path; rename detection carried those entries here.
  assert.match(log, /AGT-2942, I01 one-box wiring in progress/);
  assert.match(log, /29 September 2026 · AGT-2942 evidence correction and compose cleanup/);
  assert.match(log, /AGT-2951 prepared publication of AGT-W63/);
  assert.match(html, /Canonical concept entrypoint: <code>docs\/operations\/deployment-story\/index\.html<\/code>/);
  const decisions = between(html, '<section id="open-decisions"', '</section>');
  assert.equal((decisions.match(/data-decision-id="D\d"/g) ?? []).length, 8);
});
