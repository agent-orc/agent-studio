#!/usr/bin/env node
/** Keep single-choice buttons on app-segmented-control. Documented exceptions
 * are pinned to their button signatures so adding another site fails lint. */
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const args = process.argv.slice(2);
function arg(name, fallback) {
  const at = args.indexOf(`--${name}`);
  return at < 0 ? fallback : args[at + 1];
}
const root = arg('root', 'src');
const baselinePath = arg('baseline', 'scripts/segmented-baseline.json');
const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
const documented = baseline.documented ?? {};
const failures = [];
function files(dir) {
  return readdirSync(dir).flatMap(name => {
    const file = join(dir, name);
    return statSync(file).isDirectory() ? (name === 'mockups' ? [] : files(file)) : name.endsWith('.html') ? [file] : [];
  });
}
function signature(tag) {
  return createHash('sha256').update(tag.replace(/\s+/g, ' ').trim()).digest('hex').slice(0, 16);
}
function sites(source) {
  const clean = source.replace(/<!--[\s\S]*?-->/g, '');
  const found = [];
  for (const match of clean.matchAll(/<button\b[^>]*>/gs)) {
    const tag = match[0];
    if (tag.includes('[attr.aria-pressed]') ||
        (tag.includes('[attr.aria-checked]') && !tag.includes('role="switch"')) ||
        /\[class\.[^\]]*(?:active|selected|--on)\]="[^"]*==[^"]*"/.test(tag)) {
      found.push(signature(tag));
    }
  }
  return found.sort();
}
for (const [file, entry] of Object.entries(documented)) {
  if (typeof entry.reason !== 'string' || entry.reason.trim().length < 20 || !Array.isArray(entry.signatures)) {
    failures.push(`${file}: exception needs a written reason and button signatures.`);
  }
}
const templates = files(root);
for (const file of templates) {
  const rel = relative('.', file).split(sep).join('/');
  if (rel.endsWith('/segmented-control.component.html')) continue;
  const actual = sites(readFileSync(file, 'utf8'));
  const expected = documented[rel]?.signatures?.slice().sort() ?? [];
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    failures.push(`${rel}: hand-built selection button changed or added; use <app-segmented-control> or document a specific exception.`);
  }
}
for (const rel of Object.keys(documented)) {
  if (!templates.some(file => relative('.', file).split(sep).join('/') === rel)) {
    failures.push(`${rel}: stale exception for a missing template.`);
  }
}
if (failures.length) {
  console.error(`Segmented control check FAILED:\n${failures.map(f => '  - ' + f).join('\n')}`);
  process.exit(1);
}
console.log(`Segmented control check OK: ${Object.keys(documented).length} documented exception templates.`);
