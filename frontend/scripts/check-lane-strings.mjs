#!/usr/bin/env node
/**
 * Lane names come from one module (AGT-2715).
 *
 * The operator counted four wordings and two tones for the single lane
 * `5-human-review`: the header chip said "Review", the Result tab header said
 * "Human review lane", a badge normaliser rewrote that into "Human review",
 * and the project workflow section said "Awaiting human review." Each surface
 * had grown its own label map, so every new surface re-invented the lane.
 *
 * This gate keeps `src/app/models/lane-presentation.ts` the only place a lane
 * name is written. It fails on:
 *
 *   1. a string literal equal to a canonical lane name or role sentence
 *      (parsed live out of the presentation module, so the two cannot drift);
 *   2. a string literal matching a RETIRED wording — a phrasing that caused a
 *      reported defect and must not come back.
 *
 * Genuine non-lane uses of a word like "Review" (a runner plane role, a
 * code-review grade chip) are listed in `lane-string-allowlist.json` with a
 * reason. Adding an entry is a deliberate act; adding a bare lane string is
 * not possible without one.
 *
 * Run via `npm run lint:structure`.
 */
import { existsSync, readFileSync } from 'node:fs';
import { execSync } from 'node:child_process';

const PRESENTATION_MODULE = 'src/app/models/lane-presentation.ts';
const ALLOWLIST = 'scripts/lane-string-allowlist.json';

/**
 * Wordings that shipped, were reported as drift, and are now retired. Unlike
 * the canonical names these are matched even as a substring, because their
 * whole problem was appearing inside longer copy ("Run outcome: Human review
 * lane", "Awaiting human review.").
 */
const RETIRED_WORDINGS = [
  'Human review lane',
  'Human Review lane',
  'Awaiting human review',
  'Human Review',
  'Human Ready',
  'Auto Review',
  'Needs Human Review',
];

function readCanonicalStrings() {
  const source = readFileSync(PRESENTATION_MODULE, 'utf8');
  const names = new Set();
  const sentences = new Set();
  // The catalogue is a list of `lane(state, 'Name', 'Sentence.', tone, ...)`
  // calls; pull the first two string arguments out of each.
  const call = /\blane\(\s*[^,]+,\s*'([^']+)'\s*,\s*\n?\s*'([^']+)'/g;
  let m;
  while ((m = call.exec(source)) !== null) {
    names.add(m[1]);
    sentences.add(m[2]);
  }
  if (names.size === 0) {
    console.error(`Could not parse any lane names out of ${PRESENTATION_MODULE}.`);
    console.error('If the catalogue shape changed, update the parser in this script.');
    process.exit(1);
  }
  return { names, sentences };
}

function readAllowlist() {
  if (!existsSync(ALLOWLIST)) return [];
  const entries = JSON.parse(readFileSync(ALLOWLIST, 'utf8'));
  return entries.map((e) => ({ ...e, literal: e.literal }));
}

const { names, sentences } = readCanonicalStrings();
const banned = new Set([...names, ...sentences]);
const allowlist = readAllowlist();

const files = execSync('git ls-files --cached --others --exclude-standard src/app', { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync)
  .filter((f) => f.endsWith('.ts') || f.endsWith('.html'))
  .filter((f) => !f.endsWith('.spec.ts'))
  // The presentation module is where lane names are declared; the doc-topic
  // shim and this script's own fixtures re-export from it.
  .filter((f) => f !== PRESENTATION_MODULE);

/** True when `file`/`literal` is an approved non-lane use of the same word. */
function isAllowed(file, literal) {
  return allowlist.some((e) => e.file === file && e.literal === literal);
}

const violations = [];

for (const file of files) {
  const lines = readFileSync(file, 'utf8').split('\n');
  lines.forEach((line, index) => {
    // Skip comments: prose explaining the rule may legitimately name a lane.
    const trimmed = line.trim();
    if (trimmed.startsWith('//') || trimmed.startsWith('*') || trimmed.startsWith('/*')) return;
    if (trimmed.startsWith('<!--')) return;

    for (const retired of RETIRED_WORDINGS) {
      if (line.includes(retired) && !isAllowed(file, retired)) {
        violations.push({
          file, line: index + 1, literal: retired,
          reason: `retired wording "${retired}" — a lane is named only by lane-presentation.ts`,
        });
      }
    }

    // Exact string literals: 'Human review', "Human review", or a bare
    // template text node. Only exact matches count, so "Archive & Next" and
    // "Send to Backlog" (action labels that embed a lane word) stay legal.
    const literals = [...line.matchAll(/'([^']*)'|"([^"]*)"/g)].map((m) => m[1] ?? m[2]);
    for (const literal of literals) {
      if (!banned.has(literal)) continue;
      if (isAllowed(file, literal)) continue;
      violations.push({
        file, line: index + 1, literal,
        reason: `hard-coded lane string "${literal}" — import laneName()/laneSentence() instead`,
      });
    }
  });
}

if (violations.length > 0) {
  console.error(`\nFound ${violations.length} hard-coded lane string(s):\n`);
  for (const v of violations) {
    console.error(`  - ${v.file}:${v.line}`);
    console.error(`    ${v.reason}`);
  }
  console.error(`\nRule: a lane's name, role sentence, glyph, and tone live only in`);
  console.error(`${PRESENTATION_MODULE}. Import laneName() / laneSentence() /`);
  console.error('laneGlyph() / laneTone() rather than repeating the string.');
  console.error(`If this really is not a lane, add it to ${ALLOWLIST} with a reason.`);
  console.error('See docs/system/domains/frontend.md (Lane presentation).\n');
  process.exit(1);
}

console.log(
  `OK: scanned ${files.length} files; no hard-coded lane strings `
  + `(${banned.size} canonical, ${RETIRED_WORDINGS.length} retired, ${allowlist.length} allowlisted).`,
);
