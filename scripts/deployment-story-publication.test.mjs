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
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import test from 'node:test';

const root = resolve(import.meta.dirname, '..');
const canonicalDir = join(root, 'docs/operations/deployment-story');
const canonicalEntry = join(canonicalDir, 'index.html');
const read = (file) => readFileSync(file, 'utf8');

function htmlLinks(html) {
  return [...html.matchAll(/\b(?:href|src)="([^"]*)"/g)].map((m) => m[1]);
}

function markdownLinks(md) {
  return [...md.matchAll(/\]\(([^)\s]+)\)/g)].map((m) => m[1]);
}

function anchorIds(html) {
  return new Set([...html.matchAll(/\b(?:id|name)="([^"]+)"/g)].map((m) => m[1]));
}

// Resolves one local link from `fromFile`; returns a failure string or null.
function checkLink(fromFile, link) {
  // External URLs and root-absolute Studio routes such as /?task=AGT-2736 are
  // not repository files.
  if (/^(?:[a-z][a-z0-9+.-]*:|\/)/i.test(link)) return null;
  const [pathPart, fragment] = link.split('#');
  const target = pathPart ? resolve(dirname(fromFile), decodeURI(pathPart)) : fromFile;
  const where = `${relative(root, fromFile)} -> ${link}`;
  if (!existsSync(target)) return `${where}: missing target`;
  if (fragment && statSync(target).isFile() && target.endsWith('.html')
      && !anchorIds(read(target)).has(fragment)) {
    return `${where}: missing anchor #${fragment}`;
  }
  return null;
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

test('every local link and anchor in the relocated Dossier resolves', () => {
  const links = htmlLinks(read(canonicalEntry));
  const failures = links.map((l) => checkLink(canonicalEntry, l)).filter(Boolean);
  assert.deepEqual(failures, []);
  // Guards against the matcher silently finding nothing after a markup change.
  assert.ok(links.length >= 40, `expected the Dossier's evidence links, found ${links.length}`);
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
  const s5 = html.slice(html.indexOf('<h2 id="s5"'), html.indexOf('<h2 id="s6"'));
  const cards = [...s5.matchAll(/<a class="card" href="([^"]+)"/g)].map((m) => m[1]);
  const at = cards.indexOf('../deployment-story/index.html');
  assert.ok(at > 0, 'deployment story card missing from §5');
  assert.equal(cards[at - 1], '../operations-server-backchannel/index.html');
  assert.equal(cards[at + 1], '../docker-ausfuehrungswelt-migration/index.html');
  assert.equal(html.split('deployment-story/index.html').length - 1, 1, 'link-only: one North star mention');
  const history = html.slice(html.indexOf('<h2 id="s6"'), html.indexOf('<section id="s7"'));
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
  const section = html.slice(html.indexOf('<section id="deployment">'));
  const deployment = section.slice(0, section.indexOf('</section>'));
  assert.match(deployment, /href="\.\.\/deployment-story\/index\.html"/);
  assert.match(deployment, /href="\.\.\/\.\.\/task-server-bus\/index\.html"/);
  assert.match(deployment, /one-box special case and an ordinary runner host/);
  assert.match(deployment, /quality work is made of shared pipeline-library steps/);
  // The reconciliation defers to W49's own decision record instead of
  // restating a status that later implementation slices may change.
  assert.match(deployment, /does not change the recorded status of D1 to D4/);
  assert.match(html, /<span><b>Status<\/b> Decision pending<\/span>/);
  for (const id of ['d1-architecture', 'd2-standard-deployment', 'd3-agt-2736', 'd4-orchestrator-sessions']) {
    assert.match(html, new RegExp(`data-decision-id="${id}"`));
  }
  // The develop-side D12 execution-role amendment survives the merge.
  assert.match(html, /D12 execution-role amendment \(2026-09-26\)/);
  assert.match(html, />Workstation host adapter</);
  assert.match(html, /One-box placement on a developer machine: Web, local Connector, Task Server, Engine, native host manager, and the same runner-host service used by other hosts/);
  assert.equal(/^(?:<{7}|={7}|>{7})/m.test(html), false, 'no merge markers');
});
