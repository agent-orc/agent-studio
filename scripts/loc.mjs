#!/usr/bin/env node
/**
 * Repo-wide LOC reporting via `scc` (https://github.com/boyter/scc).
 *
 * Run from the repo root:
 *   node scripts/loc.mjs                 # full report (all langs + per-dir)
 *   node scripts/loc.mjs --lang          # languages only
 *   node scripts/loc.mjs --dirs          # per-top-level-dir only
 *   node scripts/loc.mjs --json          # machine-readable JSON
 *
 * scc is the LOC tool of choice: fast, multi-language (TS, C#, HTML,
 * SCSS, MD, JSON), gives both LOC and cyclomatic complexity. Install
 * via `winget install scc` (Windows), `brew install scc` (macOS),
 * or grab a binary from the GitHub releases page.
 */
import { spawnSync, execFileSync } from 'node:child_process';
import { existsSync as exists, readdirSync } from 'node:fs';
import { resolve, join } from 'node:path';

const args = new Set(process.argv.slice(2));
const wantLang = args.has('--lang') || (!args.has('--dirs') && !args.has('--json'));
const wantDirs = args.has('--dirs') || (!args.has('--lang') && !args.has('--json'));
const wantJson = args.has('--json');

// Patterns to skip — build outputs, vendored deps, transient state. The
// path matches scc's --not-match (regex over the file path).
const NOT_MATCH = [
  'node_modules',
  'dist',
  'out-tsc',
  'playwright-report',
  'test-results',
  '\\.angular',
  '\\.claude/worktrees',
  '\\.claude-screenshots',
  '\\.test-output',
  'bin/',
  'obj/',
  'artifacts/',
  'logs/',
].join('|');

const sccPath = locateScc();
if (!sccPath) {
  console.error(
    'scc binary not found.\n' +
    '  Install: winget install scc  |  brew install scc  |\n' +
    '           https://github.com/boyter/scc/releases'
  );
  process.exit(2);
}

const repoRoot = resolve(import.meta.dirname, '..');
process.chdir(repoRoot);

if (wantJson) {
  const out = execFileSync(sccPath, ['--format', 'json', '--not-match', NOT_MATCH, '.'], { encoding: 'utf8', windowsHide: true });
  process.stdout.write(out);
  process.exit(0);
}

if (wantLang) {
  console.log('# Languages — repo-wide\n');
  spawnSync(sccPath, ['--no-cocomo', '--not-match', NOT_MATCH, '.'], { stdio: 'inherit', windowsHide: true });
}

if (wantDirs) {
  console.log('\n# Per top-level directory (Code LOC)\n');
  // Skip build outputs / vendored deps / transient state at the top level.
  // The same paths inside subprojects are filtered by NOT_MATCH, but the
  // top-level dirs themselves need to be enumerated up front.
  const SKIP_TOP = new Set([
    'node_modules', 'dist', 'out-tsc', 'bin', 'obj', 'logs',
    'artifacts', 'playwright-report', 'test-results',
    '.angular', '.vscode', '.claude', '.claude-screenshots',
    '.test-output', '.git',
  ]);
  const tops = readdirSync('.', { withFileTypes: true })
    .filter(d => d.isDirectory())
    .map(d => d.name)
    .filter(n => !SKIP_TOP.has(n))
    .filter(n => !n.startsWith('.') || n === '.agents' || n === '.github')
    .sort();

  const rows = [];
  for (const dir of tops) {
    const out = execFileSync(
      sccPath,
      ['--no-cocomo', '--no-complexity', '--format', 'wide', '--not-match', NOT_MATCH, dir],
      { encoding: 'utf8', windowsHide: true }
    );
    const totalLine = out.split('\n').find(l => l.startsWith('Total'));
    if (!totalLine) continue;
    const cols = totalLine.trim().split(/\s+/);
    // wide format: Total <files> <lines> <blanks> <comments> <code> ...
    const files = parseInt(cols[1], 10);
    const code = parseInt(cols[5], 10);
    if (!isNaN(code) && code > 0) rows.push({ dir, files, code });
  }
  rows.sort((a, b) => b.code - a.code);

  const w = Math.max(20, ...rows.map(r => r.dir.length + 2));
  console.log(`${'Directory'.padEnd(w)}${'Files'.padStart(8)}${'Code'.padStart(12)}`);
  console.log('-'.repeat(w + 20));
  for (const r of rows) {
    console.log(`${r.dir.padEnd(w)}${String(r.files).padStart(8)}${r.code.toLocaleString('en-US').padStart(12)}`);
  }
}

function locateScc() {
  // 1. PATH
  const which = spawnSync(process.platform === 'win32' ? 'where.exe' : 'which', ['scc'], { encoding: 'utf8', windowsHide: true });
  if (which.status === 0) {
    const p = which.stdout.split(/\r?\n/).find(l => l.trim());
    if (p && exists(p.trim())) return p.trim();
  }
  // 2. Known winget install location
  if (process.platform === 'win32') {
    const home = process.env.LOCALAPPDATA;
    if (home) {
      const wingetGlob = join(home, 'Microsoft', 'WinGet', 'Packages');
      if (exists(wingetGlob)) {
        for (const pkg of readdirSync(wingetGlob)) {
          if (/scc/i.test(pkg)) {
            const candidate = join(wingetGlob, pkg, 'scc.exe');
            if (exists(candidate)) return candidate;
          }
        }
      }
    }
  }
  return null;
}
