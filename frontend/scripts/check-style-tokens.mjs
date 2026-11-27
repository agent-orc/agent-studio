import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { inventory, root, sourceFiles } from './style-inventory.mjs';

export function debt(report) {
  const entries = {};
  for (const row of report.findings.filter(row => ['colour', 'fontSize'].includes(row.category))) {
    const key = JSON.stringify([row.file, row.selector, row.property, row.category, row.literal]);
    entries[key] = (entries[key] ?? 0) + 1;
  }
  return entries;
}
export function compare(current, baseline) {
  return Object.entries(current).filter(([key, count]) => count > (baseline[key] ?? 0))
    .map(([key, count]) => `${key}: ${count} exceeds ${baseline[key] ?? 0}`);
}
export function undefinedTokens() {
  const texts = sourceFiles().filter(path => /\.(scss|css|html|ts)$/.test(path) && !/\.spec\./.test(path))
    .map(path => readFileSync(path, 'utf8')).join('\n');
  const declared = new Set([...texts.matchAll(/(--studio-[\w-]+)\s*:/g)].map(match => match[1]));
  const used = [...texts.matchAll(/var\((--studio-[\w-]+)/g)].map(match => match[1]);
  return Object.fromEntries([...new Set(used)].filter(name => !declared.has(name)).sort().map(name => [name, used.filter(value => value === name).length]));
}

export function check(current, baseline, previous) {
  const failures = compare(current.raw, baseline.raw).concat(compare(current.undefined, baseline.undefined));
  if (previous) failures.push(...compare(baseline.raw, previous.raw), ...compare(baseline.undefined, previous.undefined));
  return failures;
}

export function checkHistory(current, baseline, references) {
  return [...new Set(references.length
    ? references.flatMap(previous => check(current, baseline, previous))
    : check(current, baseline))];
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const path = new URL('./style-token-baseline.json', import.meta.url);
  const baseline = JSON.parse(readFileSync(path, 'utf8'));
  const current = { raw: debt(inventory()), undefined: undefinedTokens() };
  const git = args => execFileSync('git', args, { cwd: root, encoding: 'utf8', windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  // Gates test a committed delivery, so HEAD alone would let a baseline grow
  // in that same commit. Check its parent and the local integration merge base
  // as well. Reading refs never fetches or changes the repository.
  const revisions = new Set(['HEAD', 'HEAD^1']);
  for (const branch of ['origin/develop', 'develop']) {
    try { revisions.add(git(['merge-base', 'HEAD', branch])); } catch { /* Optional local ref. */ }
  }
  const references = [];
  for (const revision of revisions) {
    try { references.push(JSON.parse(git(['show', `${revision}:frontend/scripts/style-token-baseline.json`]))); }
    catch { /* Initial introduction has no historical baseline. */ }
  }
  const failures = checkHistory(current, baseline, references);
  if (failures.length) {
    console.error(failures.join('\n'));
    process.exitCode = 1;
  } else if (process.argv.includes('--shrink')) {
    writeFileSync(path, JSON.stringify({ ...baseline, ...current }, null, 2) + '\n');
    console.log('Style token baseline shrunk to current debt.');
  } else {
    console.log(`Style tokens: no new raw colours, font sizes or undeclared references (${Object.keys(current.raw).length} recorded signatures).`);
  }
}
