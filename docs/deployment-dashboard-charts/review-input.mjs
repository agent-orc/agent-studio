// Reproduces what the concept-fit review receives of this Dossier, so the build
// can prove nothing a reviewer must read is cut. Two inputs, ported from the
// product code; keep them in step with it:
//   1. The snapshot of index.html, workbench.json and every file they link
//      (backend/Features/Runner/ConceptDossierReviewMaterial.cs), read from the
//      Studio's own checkout, which holds a Dossier only after integration.
//   2. The diff of the delivery against the integration branch
//      (runner/SourceFacts/MergeBaseDiffMaterial.cs), cut a file and in total
//      (runner/ReviewAspectInputBudget.cs). On a first delivery this is the
//      only place the reviewer sees the text.
//   node docs/deployment-dashboard-charts/review-input.mjs [--json] [--base <ref>]
// Exit code 1 when a file a reviewer must read would be missing, cut or left out.
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, lstatSync, mkdtempSync, readFileSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, extname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const LIMITS = { files: 64, fileCharacters: 256_000, totalCharacters: 512_000,
  diffFiles: 200, diffLines: 12_000, diffFileCharacters: 40_000, diffCharacters: 580_000, promptCharacters: 800_000 };
// Build products whose text a reviewer does not need: the sources are in src/.
export const GENERATED = ['prototype.html'];
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

/** Input 1: the breadth-first walk over linked files with its three limits. */
export function reviewSnapshot(repositoryRoot, entry) {
  const root = resolve(repositoryRoot);
  const pending = [entry, entry.slice(0, entry.lastIndexOf('/')) + '/workbench.json'];
  const seen = new Set();
  const files = [];
  const problems = [];
  let remaining = LIMITS.totalCharacters;
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
      continue;
    }
    const content = readFileSync(full, 'utf8');
    if (content.length > LIMITS.fileCharacters || content.length > remaining) {
      problems.push(`${relative}: ${content.length} characters exceed the remaining ${remaining} character budget.`);
      continue;
    }
    remaining -= content.length;
    files.push({ path: relative, kind: 'text', characters: content.length });
    for (const match of content.matchAll(LINK)) {
      const link = resolveLink(relative, match[1] ?? match[2]);
      if (link && !seen.has(link)) pending.push(link);
    }
  }
  return { files, problems, inspected: seen.size, textCharacters: LIMITS.totalCharacters - remaining };
}

const PINNED = ['-c', 'credential.helper=', '-c', 'core.quotePath=false', '-c', 'diff.noprefix=false', '-c', 'diff.mnemonicPrefix=false',
  '-c', 'diff.relative=false', '-c', 'diff.algorithm=myers', '-c', 'diff.indentHeuristic=true', '-c', 'color.ui=never'];

/**
 * Input 2: the delivery diff as the review runner cuts it. The delivery is the
 * working state of `directory` (committed or not) against the merge base with
 * `integrationRef`.
 */
export function reviewDiff(repositoryRoot, directory, { integrationRef = 'origin/develop' } = {}) {
  const root = resolve(repositoryRoot);
  const git = (args, env = {}) => execFileSync('git', args, { cwd: root, env: { ...process.env, ...env }, encoding: 'utf8', maxBuffer: 1 << 28 });
  const base = git(['merge-base', 'HEAD', integrationRef]).trim();
  const temp = mkdtempSync(join(tmpdir(), 'ddc-review-'));
  let diff, changed;
  try {
    const index = join(temp, 'index');
    copyFileSync(resolve(root, git(['rev-parse', '--git-path', 'index']).trim()), index);
    const env = { GIT_INDEX_FILE: index };
    git(['add', '-A', '--', '.'], env);
    const tree = git(['write-tree'], env).trim();
    changed = git([...PINNED, 'diff', '--name-only', '--find-renames', '--no-color', base, tree]).split('\n').filter(Boolean);
    diff = git([...PINNED, 'diff', '--no-ext-diff', '--no-textconv', '--no-color', '--full-index', '--find-renames', '--unified=3', '--inter-hunk-context=0', base, tree]);
  } finally { rmSync(temp, { recursive: true, force: true }); }
  const problems = [];
  const prefix = directory.replace(/\/+$/, '') + '/';
  const outside = changed.filter(path => !path.startsWith(prefix));
  if (outside.length) problems.push(`Concept runs may change only ${prefix}: ${outside.join(', ')}`);
  const lines = diff.split('\n');
  if (lines.at(-1) === '') lines.pop();
  if (changed.length > LIMITS.diffFiles) problems.push(`${changed.length} changed files exceed the ${LIMITS.diffFiles} of the review material.`);
  if (lines.length > LIMITS.diffLines) problems.push(`${lines.length} diff lines exceed the ${LIMITS.diffLines} of the review material; the tail is dropped.`);
  // Sections in the order the budget takes them: templates and styles first, then by path.
  const sections = [];
  for (const line of lines.slice(0, LIMITS.diffLines)) {
    if (line.startsWith('diff --git ')) sections.push({ file: line.slice(line.lastIndexOf(' b/') + 3).trim(), text: '' });
    if (sections.length) sections.at(-1).text += line + '\n';
  }
  const first = path => /\.(html|scss|css|sass)$/i.test(path);
  const ordered = [...sections].sort((a, b) => (first(b.file) - first(a.file)) || (a.file < b.file ? -1 : a.file > b.file ? 1 : 0));
  let used = 0;
  const files = [];
  for (const section of ordered) {
    const binary = /^Binary files /m.test(section.text) && !/^@@ /m.test(section.text);
    const length = section.text.length;
    const available = Math.min(LIMITS.diffFileCharacters - 1, LIMITS.diffCharacters - used - 1);
    const kept = available < 300 ? 0 : Math.min(length, available);
    used += kept;
    const name = section.file.startsWith(prefix) ? section.file.slice(prefix.length) : section.file;
    const generated = GENERATED.includes(name);
    files.push({ path: section.file, characters: length, kept, generated, binary });
    if (kept < length && !generated) problems.push(`${section.file}: ${length - kept} of ${length} diff characters would be cut (limit ${LIMITS.diffFileCharacters} a file, ${LIMITS.diffCharacters} in total).`);
  }
  return { base, files, problems, changedFiles: changed.length, lines: lines.length, characters: used };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const dir = dirname(fileURLToPath(import.meta.url));
  const root = resolve(dir, '../..');
  const at = process.argv.indexOf('--base');
  const snapshot = reviewSnapshot(root, 'docs/deployment-dashboard-charts/index.html');
  const diff = reviewDiff(root, 'docs/deployment-dashboard-charts', at > 0 ? { integrationRef: process.argv[at + 1] } : {});
  const problems = [...snapshot.problems, ...diff.problems];
  if (process.argv.includes('--json')) console.log(JSON.stringify({ snapshot, diff }, null, 2));
  else {
    console.log('Snapshot of linked files (read from a checkout that holds the Dossier)');
    for (const f of snapshot.files) console.log(`${f.kind === 'text' ? String(f.characters).padStart(7) : ' binary'}  ${f.path}`);
    console.log(`${snapshot.inspected} of ${LIMITS.files} files, ${snapshot.textCharacters} of ${LIMITS.totalCharacters} characters\n`);
    console.log(`Delivery diff against ${diff.base.slice(0, 9)}, text files, in the order the budget takes them`);
    for (const f of diff.files.filter(f => !f.binary)) console.log(`${String(f.characters).padStart(7)}  ${f.kept < f.characters ? `cut to ${f.kept}` : 'whole'}${f.generated ? ' (generated)' : ''}  ${f.path}`);
    console.log(`${diff.files.filter(f => f.binary).length} binary files named, ${diff.changedFiles} of ${LIMITS.diffFiles} files, ${diff.lines} of ${LIMITS.diffLines} lines, ${diff.characters} of ${LIMITS.diffCharacters} characters`);
    for (const p of problems) console.log('PROBLEM ' + p);
  }
  process.exit(problems.length ? 1 : 0);
}
