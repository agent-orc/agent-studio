import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';

const root = resolve(import.meta.dirname, '..');
const dossier = resolve(root, 'docs/workload-identitaet/index.html');
const read = (path) => readFileSync(resolve(root, path), 'utf8');

test('the start index contains exactly one workload identity Dossier row', () => {
  const rows = read('docs/start/README.md').split('\n')
    .filter((line) => line.startsWith('|') && line.includes('workload-identitaet/index.html'));
  assert.equal(rows.length, 1);
  assert.match(rows[0], /\]\(\.\.\/workload-identitaet\/index\.html\)/);
  assert.ok(existsSync(dossier));
});

test('the operations index points to the canonical Dossier', () => {
  const rows = read('docs/operations/README.md').split('\n')
    .filter((line) => line.startsWith('|') && line.includes('workload-identitaet/index.html'));
  assert.equal(rows.length, 1);
  const link = rows[0].match(/\]\((\.\.\/workload-identitaet\/index\.html)\)/);
  assert.ok(link);
  assert.equal(resolve(root, 'docs/operations', link[1]), dossier);
});

test('IP-D10 links to the existing canonical Dossier', () => {
  const source = resolve(root, 'docs/operations/integrierte-plattform/index.html');
  const html = readFileSync(source, 'utf8');
  const start = html.indexOf('<div class="decision" data-decision-id="ip-workload-identitaet"');
  const end = html.indexOf('<div class="decision"', start + 1);
  assert.ok(start >= 0 && end > start);
  const section = html.slice(start, end);
  const link = section.match(/href="(\.\.\/\.\.\/workload-identitaet\/index\.html)"/);
  assert.ok(link, 'IP-D10 must link to the canonical Dossier');
  assert.equal(resolve(dirname(source), link[1]), dossier);
  assert.ok(existsSync(resolve(dirname(source), link[1])));
});
