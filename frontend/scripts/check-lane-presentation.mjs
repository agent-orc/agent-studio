#!/usr/bin/env node
/**
 * Enforce that lane wording lives in ONE place.
 *
 * Hard rule: outside `src/app/models/lane-presentation.ts`, no module may pair
 * a lane key with a lane display string. Read `laneName` / `laneShortName` /
 * `laneSentence` / `lanePresentation` instead.
 *
 * Why: before AGT-2715 the single lane `5-human-review` was rendered as
 * "Review" (header chip), "Human review lane" (Result header), "Human review"
 * (badge normaliser), and "Awaiting human review." (project workflow section),
 * in two different tones, because fifteen modules each declared their own
 * label map. This gate is what stops the sixteenth.
 *
 * Detection is deliberately narrow to stay false-positive free: a line is a
 * violation only when it contains BOTH
 *   (a) a lane key — `'5-human-review'` or `TaskState.HumanReview`, and
 *   (b) a quoted string that matches a known lane display name / sentence.
 * That is exactly the shape of a hand-rolled lane map:
 *   `[TaskState.HumanReview]: 'Review'`
 *   `case '5-human-review': return 'Human review';`
 *   `{ state: TaskState.Completed, label: 'Delivered' }`
 * while prose that merely mentions a lane ("Move to Delivered", "Archived")
 * carries no lane key and is left alone.
 *
 * Examples that PASS:
 *   laneOptions = [TaskState.HumanReview].map(s => ({ s, label: laneShortName(s) }))
 *   [TaskState.HumanReview]: 'lane-5-human-review'          // a doc topic, not a name
 *   { id: 'restore', label: 'Restore (→ Backlog)' }         // no lane key on the line
 *
 * Example that FAILS:
 *   const LABELS = { [TaskState.HumanReview]: 'Review' };
 *
 * The vocabulary is READ FROM `lane-presentation.ts` itself, so renaming a lane
 * there moves the gate with it. `LEGACY_VARIANTS` below additionally keeps the
 * spellings we have already retired from coming back.
 */
import { existsSync, readFileSync } from 'node:fs';
import { execSync } from 'node:child_process';

const SOURCE_OF_TRUTH = 'src/app/models/lane-presentation.ts';
const root = 'src/app';

/** Lane keys, as they appear in `TaskState` (models/task.model.ts). */
const LANE_KEYS = [
  '0-backlog', '1-preparation', '1a-orchestrator-prep', '1b-needs-human-review',
  '2-ready', '2-ready-intake', '3-progress', '3a-failed-pickup',
  '3b-code-not-complete', '4-review', '4-auto-review', '5-human-review',
  '5e-escalated', '6-completed', '7-archive',
];

/** `TaskState.*` members that name a lane. */
const LANE_MEMBERS = [
  'Backlog', 'Preparation', 'OrchestratorPrep', 'Ready', 'Progress',
  'FailedPickup', 'CodeNotComplete', 'AutoReview', 'Escalated', 'HumanReview',
  'Completed', 'Archive',
];

/**
 * Retired spellings. Not present in `lane-presentation.ts` any more, and each
 * one was a real drift we fixed — keep them named so they fail loudly if they
 * are reintroduced.
 */
const LEGACY_VARIANTS = [
  'Review', 'Human Review', 'Human review lane', 'Completed lane',
  'Needs Human Review', 'Post processing', 'In progress', 'Progress',
  'Orchestrator prep', 'Awaiting human review',
  'The task is waiting for a human decision',
];

const laneKeyPattern = new RegExp(
  `(?:'(?:${LANE_KEYS.join('|')})'|"(?:${LANE_KEYS.join('|')})"|\\bTaskState\\.(?:${LANE_MEMBERS.join('|')})\\b)`,
);
const stringLiteralPattern = /'([^'\\\n]*)'|"([^"\\\n]*)"/g;

/** Lowercase, drop a trailing period, collapse whitespace. */
function normalise(text) {
  return text.trim().toLowerCase().replace(/\s+/g, ' ').replace(/\.$/, '');
}

/**
 * Pull the display vocabulary out of the source of truth. Each entry there is
 * a `lane(key, name, shortName, sentence, toneSlug, glyph, docTopic)` call.
 */
function readLaneVocabulary() {
  if (!existsSync(SOURCE_OF_TRUTH)) {
    console.error(`\nLane presentation module not found at ${SOURCE_OF_TRUTH}.`);
    console.error('The lane-wording gate cannot run without it.\n');
    process.exit(1);
  }
  const text = readFileSync(SOURCE_OF_TRUTH, 'utf8');
  const call = /\blane\(\s*[^,]+,\s*'([^']*)',\s*'([^']*)',\s*'([^']*)',/g;
  const phrases = new Set();
  let lanes = 0;
  let match;
  while ((match = call.exec(text)) !== null) {
    lanes++;
    for (const phrase of [match[1], match[2], match[3]]) {
      if (phrase) phrases.add(normalise(phrase));
    }
  }
  // Guard against a silently-weakened gate: if the module is reformatted so the
  // regex stops matching, fail rather than pass everything.
  if (lanes < LANE_MEMBERS.length) {
    console.error(`\nRead only ${lanes} lane definitions from ${SOURCE_OF_TRUTH} (expected at least ${LANE_MEMBERS.length}).`);
    console.error('The `lane(...)` call shape changed; update the extraction in this script so the gate keeps working.\n');
    process.exit(1);
  }
  for (const variant of LEGACY_VARIANTS) phrases.add(normalise(variant));
  return phrases;
}

const phrases = readLaneVocabulary();

const files = execSync(`git ls-files --cached --others --exclude-standard ${root}`, { encoding: 'utf8' })
  .split('\n')
  .filter(existsSync)
  .filter(f => f.endsWith('.ts') && !f.endsWith('.spec.ts') && !f.endsWith('.d.ts'))
  .filter(f => f !== SOURCE_OF_TRUTH);

const violations = [];

for (const file of files) {
  const lines = readFileSync(file, 'utf8').split('\n');
  lines.forEach((line, index) => {
    const code = line.replace(/\/\/.*$/, '');
    if (!laneKeyPattern.test(code)) return;
    stringLiteralPattern.lastIndex = 0;
    let match;
    while ((match = stringLiteralPattern.exec(code)) !== null) {
      const literal = match[1] ?? match[2];
      if (!literal) continue;
      if (LANE_KEYS.includes(literal)) continue;
      // Display text is capitalised; an all-lowercase literal next to a lane
      // key is an identifier, not a label — `STATE_TO_LANE`'s `'backlog'`
      // property name, a search alias like `'humanreview'`, an action id.
      // Matching those would make the gate unusable.
      if (!/^[A-Z]/.test(literal.trim())) continue;
      if (phrases.has(normalise(literal))) {
        violations.push({ file, line: index + 1, literal, source: line.trim() });
        break;
      }
    }
  });
}

if (violations.length > 0) {
  console.error(`\nFound ${violations.length} hard-coded lane string(s):\n`);
  for (const v of violations) {
    console.error(`  - ${v.file}:${v.line}`);
    console.error(`    ${v.source}`);
    console.error(`    "${v.literal}" names a lane, next to a lane key.`);
  }
  console.error('\nRule: lane wording, tone, and glyphs live only in');
  console.error(`${SOURCE_OF_TRUTH}. Call laneName() / laneShortName() /`);
  console.error('laneSentence() / lanePresentation() instead of writing the string here,');
  console.error('so every surface names a lane the same way.');
  console.error('See docs/system/domains/frontend.md (Lane Presentation Contract).\n');
  process.exit(1);
}

console.log(`OK: scanned ${files.length} .ts files against ${phrases.size} lane phrases; no hard-coded lane strings.`);
