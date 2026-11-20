// Reproduces the snapshot the concept-fit review reads of this Dossier, so the
// build can prove the review sees all of it. Port of
// backend/Features/Runner/ConceptDossierReviewMaterial.cs (same limits, same
// link walk); keep the two in step. Run from anywhere:
//   node docs/live-structure-canvas/review-snapshot.mjs [--json]
// Exit code 1 when the snapshot would name a text file as missing, over its
// budget or not inspected.
import { existsSync, readFileSync, statSync, lstatSync } from 'node:fs';
import { dirname, resolve, sep, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

export const LIMITS = { files: 64, fileCharacters: 256_000, totalCharacters: 512_000, span: 540_000 };
const TEXT = new Set(['.html', '.htm', '.md', '.json', '.txt', '.css', '.js', '.svg', '.csv', '.yml', '.yaml']);
const LINK = /(?:href|src)\s*=\s*["']([^"']+)["']|\]\(([^)]+)\)/gi;
const decode = s => s.replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>');

function resolveLink(source, target) {
  target = decode(target).trim();
  const marker = target.search(/[#?]/);
  if (marker >= 0) target = target.slice(0, marker);
  if (!target || target.includes(':') || target.startsWith('/')) return null;
  try { target = decodeURIComponent(target); } catch { /* keep as written */ }
  target = target.replace(/\\/g, '/');
  const combined = target.startsWith('docs/') ? target : source.slice(0, source.lastIndexOf('/')).replace(/\/+$/, '') + '/' + target;
  const parts = [];
  for (const part of combined.split('/')) {
    if (part === '' || part === '.') continue;
    if (part === '..') { if (!parts.length) return null; parts.pop(); } else parts.push(part);
  }
  return parts.join('/');
}

function hasLink(root, path) {
  for (let current = path; current.length > root.length; current = dirname(current)) {
    if (existsSync(current) && lstatSync(current).isSymbolicLink()) return true;
  }
  return false;
}

export function reviewSnapshot(repositoryRoot, entry) {
  const root = resolve(repositoryRoot);
  const pending = [entry, entry.slice(0, entry.lastIndexOf('/')) + '/workbench.json'];
  const seen = new Set();
  const files = [];
  const problems = [];
  let remaining = LIMITS.totalCharacters;
  let characters = 0;
  while (pending.length) {
    if (seen.size >= LIMITS.files) {
      problems.push(`File limit reached (${LIMITS.files}); ${pending.filter(p => !seen.has(p)).length} queued links were not inspected.`);
      break;
    }
    const relative = pending.shift();
    if (seen.has(relative)) continue;
    seen.add(relative);
    const full = resolve(root, relative);
    if (!full.startsWith(root + sep) || hasLink(root, full)) { problems.push(`${relative}: outside the repository or a filesystem link.`); continue; }
    if (!existsSync(full) || !statSync(full).isFile()) { problems.push(`${relative}: linked file is missing.`); continue; }
    const bytes = statSync(full).size;
    const isText = TEXT.has(extname(relative).toLowerCase());
    if (bytes > LIMITS.fileCharacters * 4 || !isText) {
      files.push({ path: relative, kind: 'binary', bytes });
      if (isText) problems.push(`${relative}: ${bytes} bytes exceed the text budget.`);
      characters += relative.length + 60;
      continue;
    }
    const content = readFileSync(full, 'utf8');
    if (content.length > LIMITS.fileCharacters || content.length > remaining) {
      problems.push(`${relative}: ${content.length} characters exceed the remaining ${remaining} character budget.`);
      continue;
    }
    remaining -= content.length;
    characters += content.length + relative.length + 50;
    files.push({ path: relative, kind: 'text', characters: content.length });
    for (const match of content.matchAll(LINK)) {
      const link = resolveLink(relative, match[1] ?? match[2]);
      if (link && !seen.has(link)) pending.push(link);
    }
  }
  return { files, problems, inspected: seen.size, textCharacters: LIMITS.totalCharacters - remaining, spanCharacters: characters };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const dir = dirname(fileURLToPath(import.meta.url));
  const result = reviewSnapshot(resolve(dir, '../..'), 'docs/live-structure-canvas/index.html');
  if (process.argv.includes('--json')) console.log(JSON.stringify(result, null, 2));
  else {
    for (const f of result.files) console.log(`${f.kind === 'text' ? String(f.characters).padStart(7) : ' binary'}  ${f.path}`);
    console.log(`\n${result.inspected} of ${LIMITS.files} files, ${result.textCharacters} of ${LIMITS.totalCharacters} characters`);
    for (const p of result.problems) console.log('PROBLEM ' + p);
  }
  process.exit(result.problems.length ? 1 : 0);
}
