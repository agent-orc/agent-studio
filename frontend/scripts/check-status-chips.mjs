#!/usr/bin/env node
/** Keep state labels on app-status-indicator (AGT-3403). A hand-built status
 * chip is an element styled as a pill, badge, chip or dot whose look follows a
 * state: a tone, state or status data attribute, a tone modifier class, or a
 * computed class. Documented exceptions (tags, filters, counts, lane tones,
 * dense marks) are pinned to their element signatures so a new site fails. */
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const args = process.argv.slice(2);
function arg(name, fallback) {
  const at = args.indexOf(`--${name}`);
  return at < 0 ? fallback : args[at + 1];
}
const root = arg('root', 'src');
const baselinePath = arg('baseline', 'scripts/status-chip-baseline.json');
const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
const documented = baseline.documented ?? {};
const failures = [];

const CHIP_CLASS = /(?:^|[_-])(?:pill|badge|chip|dot)(?:--[\w-]+)?$/;
const TONE_WORDS = 'ok|success|warn|warning|error|danger|fail|failed|passed|running|pending|healthy|unhealthy|good|bad|stale|blocked|offline|online|live|alert|critical';
const TONE_SIGNALS = [
  /\[attr\.data-(?:tone|state|status|severity|health|result|verdict|outcome)\]/,
  /\sdata-(?:tone|state|status|severity|health|result|verdict|outcome)="/,
  new RegExp(`\\[class\\.[\\w-]*--(?:${TONE_WORDS})\\]`),
  new RegExp(`\\bclass="[^"]*--(?:${TONE_WORDS})\\b`),
  /\[class\]="/,
  /\[ngClass\]="/,
];

function files(dir) {
  return readdirSync(dir).flatMap(name => {
    const file = join(dir, name);
    return statSync(file).isDirectory() ? (name === 'mockups' ? [] : files(file)) : name.endsWith('.html') ? [file] : [];
  });
}
function signature(tag) {
  return createHash('sha256').update(tag.replace(/\s+/g, ' ').trim()).digest('hex').slice(0, 16);
}
function isStatusChip(tag) {
  const classes = (tag.match(/\sclass="([^"]*)"/)?.[1] ?? '').split(/\s+/).filter(Boolean);
  return classes.some(name => CHIP_CLASS.test(name)) && TONE_SIGNALS.some(signal => signal.test(tag));
}
function sites(source) {
  const clean = source.replace(/<!--[\s\S]*?-->/g, '');
  const found = [];
  for (const match of clean.matchAll(/<(?!app-|\/)[a-z][\w-]*\b(?:[^>"]|"[^"]*")*>/gs)) {
    if (isStatusChip(match[0])) found.push(signature(match[0]));
  }
  return found.sort();
}

for (const [file, entry] of Object.entries(documented)) {
  if (typeof entry.reason !== 'string' || entry.reason.trim().length < 20 || !Array.isArray(entry.signatures)) {
    failures.push(`${file}: exception needs a written reason and element signatures.`);
  }
}
const templates = files(root);
for (const file of templates) {
  const rel = relative('.', file).split(sep).join('/');
  if (rel.endsWith('/status-indicator.component.html')) continue;
  const actual = sites(readFileSync(file, 'utf8'));
  const expected = documented[rel]?.signatures?.slice().sort() ?? [];
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    failures.push(`${rel}: hand-built status chip changed or added; use <app-status-indicator> `
      + `(docs/quality/design/status-indicator.md) or document a specific exception. Signatures now: ${JSON.stringify(actual)}`);
  }
}
for (const rel of Object.keys(documented)) {
  if (!templates.some(file => relative('.', file).split(sep).join('/') === rel)) {
    failures.push(`${rel}: stale exception for a missing template.`);
  }
}
if (failures.length) {
  console.error(`Status chip check FAILED:\n${failures.map(f => '  - ' + f).join('\n')}`);
  process.exit(1);
}
console.log(`Status chip check OK: ${Object.keys(documented).length} documented exception templates.`);
