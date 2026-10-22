#!/usr/bin/env node
// Idempotent seed for the slim DEV demo datastore (ADR-0056).
//
// Builds a small, reproducible TaskRepository the dev backend can point at
// instead of the heavy production workspace (~1300 tasks / ~650 MB). The
// generated store is built from the committed, sanitized pinned snapshot and
// holds tasks per lane across two demo projects. A subset has run / token
// history so the statistics views have data.
//
// Usage:
//   node scripts/seed-demo-workspace.mjs [--root <path>] [--force]
//
// Default root: C:\Projects\agent-taskboard-workspace-demo
//   (override with --root <path> or the ATP_DEMO_ROOT env var).
//
// Re-running RESETS the demo store to a clean, known stand: every path this
// script manages (projects/, .metadata/, logs/, and the workspace-root usage /
// settings files) is removed and rewritten. It never touches anything else under the
// root, so an operator-added .git stays put. The registry under
// .metadata/projects.json is intentionally NOT written here — the backend
// seeds it from WatchPaths on first boot (ADR-0042); wiping it forces a fresh,
// deterministic registry on the next dev start.

import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, utimesSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const DEFAULT_ROOT = 'C:\\Projects\\agent-taskboard-workspace-demo';
const OWNER = 'local-default';

// Every recorded execution says what it is. A visitor must never have to infer
// from the UI alone that a run is replayed rather than live.
const PROVENANCE = 'pinned-demo-simulated';

function parseArgs(argv) {
  const args = { root: process.env.ATP_DEMO_ROOT || DEFAULT_ROOT };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--root') args.root = argv[++i];
  }
  return args;
}

const PINNED_SNAPSHOT_PATH = process.env.ATP_DEMO_PINNED_SEED
  ? resolve(process.env.ATP_DEMO_PINNED_SEED)
  : fileURLToPath(new URL('./presentation-capture/pinned-seed.json', import.meta.url));
const PINNED_SNAPSHOT = JSON.parse(readFileSync(PINNED_SNAPSHOT_PATH, 'utf8'));

// Second pinned input of the same seed family: the invented Wiki trees and the
// Dossier gallery. It is authored rather than exported, because no real
// workspace may contribute knowledge content to a publicly browsable instance.
const PINNED_CONTENT_PATH = fileURLToPath(new URL('./presentation-capture/pinned-demo-content.json', import.meta.url));
const PINNED_CONTENT = JSON.parse(readFileSync(PINNED_CONTENT_PATH, 'utf8'));

// The canonical article document is the house style for every Dossier, so the
// demo gallery renders from it instead of inventing a second colour system.
const ARTICLE_TEMPLATE_PATH = fileURLToPath(
  new URL('../docs/app/templates/article-document-v2.html', import.meta.url));
const ARTICLE_TEMPLATE = readFileSync(ARTICLE_TEMPLATE_PATH, 'utf8');

// Deterministic timestamps so a re-seed produces byte-identical files and the
// UI never derives capture-visible dates from a re-seed's wall clock.
const BASE = Date.parse(PINNED_SNAPSHOT.fixedTimeBase);
function iso(offsetMinutes) {
  return new Date(BASE + offsetMinutes * 60_000).toISOString();
}

/** Second-precision UTC stamp, the lifecycle timestamp form descriptors use. */
function isoSeconds(offsetMinutes) {
  return `${iso(offsetMinutes).slice(0, 19)}Z`;
}

// ---- Fixture definition -------------------------------------------------

const PROJECTS = PINNED_SNAPSHOT.projects;
const TASKS = PINNED_SNAPSHOT.tasks;
const DECISION = PINNED_SNAPSHOT.decision;
const WIKI_TREES = PINNED_CONTENT.wiki;
const DOSSIERS = PINNED_CONTENT.dossiers;
const CARD_WORKBENCH_REFS = PINNED_CONTENT.cardWorkbenchReferences;
const CARD_WIKI_PAGES = PINNED_CONTENT.cardWikiPages;

// ---- Writers ------------------------------------------------------------

function writeJson(path, obj) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(obj, null, 2) + '\n', 'utf8');
  stamp(path);
}

function writeText(path, text) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, text, 'utf8');
  stamp(path);
}

function writeJsonl(path, rows) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, rows.map((r) => JSON.stringify(r)).join('\n') + '\n', 'utf8');
  stamp(path);
}

function writeBuffer(path, body) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, body);
  stamp(path);
}

function stamp(path) {
  const at = new Date(BASE);
  utimesSync(path, at, at);
}

function bucket(key) {
  const n = parseInt(key.split('-').pop(), 10) || 0;
  return String(Math.floor(n / 1000)).padStart(3, '0');
}

function slug(task) {
  return task.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}

function taskDir(root, task) {
  return join(root, 'projects', task.project, 'tasks', bucket(task.key), task.key);
}

function writeTask(root, task, index) {
  const dir = taskDir(root, task);
  const id = slug(task);
  const created = iso(index * 30);
  const entered = iso(index * 30 + 120);

  const json = {
    id,
    title: task.title,
    createdAt: created,
    enteredLaneAt: entered,
    state: task.state,
    order: task.decision ? 1 : (index + 1) * 10,
    agent: 'claude',
    ownerClientId: OWNER,
    model: 'claude-opus-4-8',
    thinkingLevel: 'medium',
    cliType: 'claude',
    kind: 'task',
    mode: 'coding',
    allowWebAccess: false,
    taskType: task.type,
    key: task.key,
    noBranchExpected: task.state === '6-completed' || task.state === '7-archive',
  };

  if (task.history) {
    json.sessionName = `demo-session-${task.key.toLowerCase()}`;
    json.lastProgressAt = iso(index * 30 + 110);
    json.lastUsage = {
      At: iso(index * 30 + 110),
      Tokens: null,
      Changes: '+128 -34',
      Requests: '2 Premium (3m 54s)',
    };
    if (task.key === 'DEMO-5') {
      json.tags = ['demo', 'frontend', 'code-review:concerns', 'code-review:grade-b'];
    }
  }

  // Canonical card edge into the Dossier gallery. relatedTaskKeys on the
  // descriptor remains populated as the current compatibility bridge.
  const workbenchRefs = CARD_WORKBENCH_REFS[task.key];
  if (workbenchRefs) json.references = { workbenches: workbenchRefs };

  const wikiPages = CARD_WIKI_PAGES[task.key];
  if (wikiPages) {
    json.relatedWikiPages = wikiPages.map((relPath) => {
      const page = wikiPage(task.project, relPath);
      return {
        // Repository-relative, like every page reference the product writes:
        // the scanner resolves existence against the repository root.
        relPath: `docs/${relPath}`,
        title: page.title,
        linkedAt: iso(page.offsetMinutes),
        source: 'manual',
      };
    });
  }

  if (task.decision) {
    json.tags = ['demo', 'frontend', 'code-review:concerns', 'code-review:grade-b'];
    json.commits = [
      {
        sha: 'd3e0c9f84a1b7e62551a7a51c3b84cf6d2a09871',
        shortSha: 'd3e0c9f8',
        message: 'feat(reports): add bounded CSV export',
        filesChanged: DECISION.diffFiles.length,
        files: DECISION.diffFiles,
        at: iso(274),
        attribution: 'task-key',
        attributionConfidence: 'high'
      }
    ];
  }

  writeJson(join(dir, 'task.json'), json);
  writeText(join(dir, 'prompt.md'), task.decision
    ? DECISION.promptMarkdown
    : `# ${task.title}\n\nPinned demo data. This card belongs to the deterministic two-project demo scene; its content is invented and safe to reset. A re-seed restores the exact captured state.\n`);

  if (task.history) writeHistory(dir, task, id, index);
  if (task.key === 'DEMO-5') {
    writeReviewEvidence(dir);
    writeArtifactGalleryScene(dir);
  }
  if (task.decision) writeDecisionState(dir, task, id);
}

function writeReviewEvidence(dir) {
  writeText(
    join(dir, 'code-review-2026-06-01T12-00-00.md'),
    `---
type: code-review-grade
runAt: ${iso(260)}
model: claude-haiku-4-5
cliType: claude
commit: demo000000000000000000000000000000000000
grade: B
verdict: concerns
summary: Export flow is ready for human review; add one empty-state assertion before release.
tag: code-review:grade-b
---

# Code Review - Quality Grade: B

> Export flow is ready for human review; add one empty-state assertion before release.

## Findings

- **Medium:** Add an assertion for exporting an empty report table.
- **Verified:** CSV escaping and timezone formatting have deterministic coverage.
`
  );
}

function writeArtifactGalleryScene(dir) {
  const images = [
    ['gallery-dashboard-light.png', 0],
    ['gallery-dashboard-dark.png', 1],
    ['gallery-export-dialog.png', 2],
    ['gallery-empty-state.png', 3],
    ['gallery-filter-panel.png', 4],
    ['gallery-mobile-table.png', 5],
    ['gallery-review-light.png', 6],
    ['gallery-review-dark.png', 7],
  ];
  for (const [fileName, variant] of images) {
    writeBuffer(join(dir, 'results', fileName), createGalleryPng(720, 450, variant));
  }

  writeText(
    join(dir, 'results', 'delivery.diff'),
    `diff --git a/frontend/src/app/reports/export.ts b/frontend/src/app/reports/export.ts
index 6a70b17..d3e0c9f 100644
--- a/frontend/src/app/reports/export.ts
+++ b/frontend/src/app/reports/export.ts
@@ -18,0 +19,5 @@
+export function escapeCsvCell(value: string): string {
+  const normalized = value.replaceAll('"', '""');
+  return /[",\\n]/.test(normalized) ? '"' + normalized + '"' : normalized;
+}
`
  );
  writeText(
    join(dir, 'results', 'gallery-notes.md'),
    `# Gallery review notes

The CSV export scene is pinned demo data, captured once and replayed unchanged.

- Light and dark views use the same deterministic report fixture.
- The dialog, empty state, filter panel, and mobile table are included.
- The delivery diff below the image grid is ready for operator review.
`
  );
  writeJson(join(dir, 'results', 'gallery-metrics.json'), {
    scene: 'activity-artifact-gallery',
    pinned: true,
    imageCount: images.length,
    themes: ['light', 'dark'],
    generatedAt: iso(260),
  });
  writeText(
    join(dir, 'results', 'gallery-run.log'),
    `[12:20:00.000] capture light theme: ok
[12:20:04.000] capture dark theme: ok
[12:20:05.000] artifact manifest: pinned
`
  );

  const artifactMessage = [
    'The pinned report-gallery evidence is ready for presentation:',
    ...images.map(([fileName]) => `- [${fileName}](results/${fileName})`),
    '- [Delivery diff](results/delivery.diff)',
    '- [Gallery notes](results/gallery-notes.md)',
    '- [Gallery metrics](results/gallery-metrics.json)',
    '- [Capture log](results/gallery-run.log)',
  ].join('\n');
  const frame = JSON.stringify({
    type: 'item.completed',
    item: { id: 'demo-artifact-gallery', type: 'agent_message', text: artifactMessage },
  });
  writeText(
    join(dir, 'logs', 'cli-output.log'),
    `[12:20:06.000] [stdout] ${frame}\n[12:20:07.000] [stdout] [[TASK_DONE]]\n`,
  );
}

function writeDecisionState(dir, task, id) {
  const reviewFile = 'code-review-grade-2026-08-09T12-32-00Z.md';
  writeText(
    join(dir, reviewFile),
    `---
type: code-review-grade
runAt: ${iso(272)}
model: gpt-5.4-mini
cliType: codex
thinkingLevel: medium
commit: d3e0c9f84a1b7e62551a7a51c3b84cf6d2a09871
grade: B
verdict: concerns
summary: ${DECISION.reviewSummary}
tag: code-review:grade-b
---

# Code Review - Quality Grade: B

> ${DECISION.reviewSummary}

## Findings

${DECISION.reviewFindings.map((finding) => `- **Medium:** ${finding}`).join('\n')}

## Verified

- CSV escaping and timezone formatting passed focused unit coverage.
- The attached browser capture was reviewed in light and dark themes.
`
  );

  const assessments = DECISION.reviewFindings.map((finding) => ({
    finding,
    action: 'Escalate',
    reason: 'The bounded automatic review budget is exhausted; the operator owns the release decision.'
  }));
  const councilReaction = {
    createdAt: iso(273),
    reviewFileName: reviewFile,
    grade: 'B',
    disposition: 'Escalate',
    summary: `Escalate ${assessments.length} open review findings; loop budget exhausted.`,
    assessments,
    startsNewRound: false,
    targetJobId: null,
    targetRunAttempt: null
  };
  writeJson(join(dir, `${reviewFile}.council-reaction.json`), councilReaction);

  writeText(
    join(dir, 'orchestrator-follow-up.md'),
    `# Operator decision handoff

The delivery is bounded and reviewable. Inspect the attached diff, browser proof, and Grade B verdict, then choose whether to request the one focused assertion, accept as-is, or abort.

${DECISION.reviewFindings.map((finding) => `- ${finding}`).join('\n')}
`
  );
  writeText(
    join(dir, 'status.md'),
    `# Status
Result: Needs decision
Case: review
Model: codex / gpt-5.4

## Overview
The reports export is implemented and verified. One bounded empty-state assertion remains for the operator to accept or return.

## What Was Done
- Added deterministic CSV escaping and timezone formatting.
- Attached the delivery diff, focused verification, and browser proof.
- Recorded the Grade B review and explicit escalation verdict.

## Tests
- Focused unit tests: 18 passed.
- Playwright: light and dark evidence captured.

## Open Items
- Decide whether the empty-table assertion blocks release.

## Images
- ![](../results/reports-export--real.png) (source: real)
`
  );
  writeJson(join(dir, 'aspect-tests-and-evidence.json'), {
    schemaVersion: 1,
    aspect: 'tests-and-evidence',
    verdict: 'concerns',
    summary: 'Focused tests passed; one explicit empty-table regression assertion remains for operator review.',
    findings: DECISION.reviewFindings,
    evidence: ['results/reports-export--real.png', 'results/delivery.diff']
  });

  const screenshot = createEvidencePng(720, 405);
  writeBuffer(join(dir, 'results', 'reports-export--real.png'), screenshot);
  writeBuffer(join(dir, 'attachments', 'export-layout.png'), screenshot);
  writeText(
    join(dir, 'results', 'delivery.diff'),
    `diff --git a/${DECISION.diffFiles[0]} b/${DECISION.diffFiles[0]}
index 6a70b17..d3e0c9f 100644
--- a/${DECISION.diffFiles[0]}
+++ b/${DECISION.diffFiles[0]}
@@ -18,0 +19,4 @@
+export function escapeCsvCell(value: string): string {
+  const normalized = value.replaceAll('"', '""');
+  return /[",\\n]/.test(normalized) ? '"' + normalized + '"' : normalized;
+}
diff --git a/${DECISION.diffFiles[1]} b/${DECISION.diffFiles[1]}
index 0b15ad0..d3e0c9f 100644
--- a/${DECISION.diffFiles[1]}
+++ b/${DECISION.diffFiles[1]}
@@ -11,0 +12,2 @@
+it('escapes commas and quotes', () => expect(exportRow(fixture)).toMatchSnapshot());
+it.todo('keeps the empty-table export disabled');
`
  );
  writeJsonl(join(dir, 'results', 'review-evidence.jsonl'), [
    {
      id: 'review-empty-table-assertion',
      source: 'code-review',
      severity: 'warn',
      title: 'One focused empty-table assertion remains',
      body: DECISION.reviewFindings[0],
      createdAt: iso(272),
      runIndex: 1,
      artifacts: ['results/reports-export--real.png', 'results/delivery.diff'],
      fileRefs: [`${DECISION.diffFiles[1]}:13`],
      acknowledged: false,
      followupJobId: null
    },
    {
      id: 'browser-proof-both-themes',
      source: 'task-check',
      severity: 'info',
      title: 'Browser proof attached in both themes',
      body: 'The export affordance, review state, and evidence surface remain readable in light and dark themes.',
      createdAt: iso(271),
      runIndex: 1,
      artifacts: ['results/reports-export--real.png'],
      fileRefs: [DECISION.diffFiles[0]],
      acknowledged: true,
      followupJobId: null
    }
  ]);
  writeJson(join(dir, 'results', 'decision.json'), {
    version: 1,
    id: 'reports-export-release',
    title: DECISION.decisionTitle,
    question: DECISION.decisionQuestion,
    context: DECISION.decisionContext,
    recommendation: {
      optionId: 'request-assertion',
      reason: 'Request the one focused assertion, then return directly to this release decision.'
    },
    options: [
      {
        id: 'request-assertion',
        label: 'Request one assertion',
        summary: 'Return the task for the named empty-table regression test only.',
        consequences: ['Keeps the current implementation.', 'Adds one focused run before release.'],
        action: { kind: 'steer', prompt: 'Add only the missing empty-table export assertion, run the focused tests, and preserve the attached evidence.' }
      },
      {
        id: 'accept-as-is',
        label: 'Accept as-is',
        summary: 'Ship the reviewed implementation without another run.',
        consequences: ['The existing coverage remains the release boundary.', 'The open finding is recorded as accepted risk.'],
        action: { kind: 'move', targetState: '6-completed' }
      },
      {
        id: 'abort-release',
        label: 'Abort release',
        summary: 'Archive the delivery and keep its evidence for later.',
        consequences: ['No export reaches release.', 'The task remains auditable in Archive.'],
        action: { kind: 'move', targetState: '7-archive' }
      }
    ],
    steer: {
      label: 'Additional guidance',
      placeholder: 'Optional constraints for the selected path',
      required: false
    }
  });

  writeJsonl(join(dir, 'logs', 'timeline.jsonl'), [
    { ts: iso(250), kind: 'prompt_created', actor: `human:${OWNER}`, payloadRef: 'prompt.md', summary: `Task created: ${task.title}`, details: { targetState: '0-backlog', agent: 'codex', provenance: PROVENANCE } },
    { ts: iso(252), kind: 'agent_run_started', actor: 'system', summary: 'codex CLI start', details: { cli: 'codex', intent: 'start', resumed: 'false', provenance: PROVENANCE } },
    { ts: iso(270), kind: 'agent_run_finished', actor: 'agent', summary: 'codex run completed', details: { cli: 'codex', status: 'completed', provenance: PROVENANCE } },
    { ts: iso(272), kind: 'code_review_grade_completed', actor: 'review', summary: 'Quality grade B with one focused gap', details: { grade: 'B', verdict: 'concerns', reviewFile, provenance: PROVENANCE } },
    { ts: iso(273), kind: 'orchestrator_escalated', actor: 'orchestrator', summary: 'Escalated for operator decision', details: { reason: '[review-loop-budget-exhausted] One focused assertion remains.', cause: 'completion-gate', attempt: '3', maxAttempts: '3', provenance: PROVENANCE } }
  ]);
}

function createEvidencePng(width, height) {
  const rowSize = width * 4 + 1;
  const raw = Buffer.alloc(rowSize * height);
  const fill = (x, y, w, h, color) => {
    for (let yy = Math.max(0, y); yy < Math.min(height, y + h); yy++) {
      for (let xx = Math.max(0, x); xx < Math.min(width, x + w); xx++) {
        const offset = yy * rowSize + 1 + xx * 4;
        raw[offset] = color[0];
        raw[offset + 1] = color[1];
        raw[offset + 2] = color[2];
        raw[offset + 3] = color[3] ?? 255;
      }
    }
  };
  fill(0, 0, width, height, [20, 23, 31, 255]);
  fill(28, 24, width - 56, 42, [37, 43, 57, 255]);
  fill(48, 38, 142, 13, [119, 165, 255, 255]);
  fill(28, 88, 184, height - 116, [29, 34, 45, 255]);
  fill(232, 88, width - 260, height - 116, [29, 34, 45, 255]);
  for (let index = 0; index < 5; index++) {
    fill(48, 112 + index * 47, 132, 12, index === 2 ? [87, 214, 185, 255] : [105, 115, 137, 255]);
    fill(252, 112 + index * 47, width - 320, 12, [77 + index * 6, 88 + index * 6, 108 + index * 5, 255]);
    fill(width - 92, 108 + index * 47, 34, 20, index === 2 ? [44, 118, 99, 255] : [50, 57, 72, 255]);
  }
  for (let y = 0; y < height; y++) raw[y * rowSize] = 0;
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0))
  ]);
}

function createGalleryPng(width, height, variant) {
  const rowSize = width * 4 + 1;
  const raw = Buffer.alloc(rowSize * height);
  const palettes = [
    [[241, 244, 249, 255], [255, 255, 255, 255], [62, 99, 221, 255]],
    [[20, 23, 31, 255], [31, 36, 48, 255], [119, 165, 255, 255]],
    [[239, 243, 250, 255], [255, 255, 255, 255], [33, 161, 121, 255]],
    [[24, 28, 38, 255], [36, 42, 56, 255], [238, 178, 70, 255]],
  ];
  const [background, surface, accent] = palettes[variant % palettes.length];
  const fill = (x, y, w, h, color) => {
    for (let yy = Math.max(0, y); yy < Math.min(height, y + h); yy++) {
      for (let xx = Math.max(0, x); xx < Math.min(width, x + w); xx++) {
        const offset = yy * rowSize + 1 + xx * 4;
        raw[offset] = color[0];
        raw[offset + 1] = color[1];
        raw[offset + 2] = color[2];
        raw[offset + 3] = color[3];
      }
    }
  };
  fill(0, 0, width, height, background);
  fill(24, 22, width - 48, 42, surface);
  fill(44, 37, 118 + variant * 7, 12, accent);
  fill(24, 84, 164, height - 108, surface);
  fill(208, 84, width - 232, height - 108, surface);
  for (let row = 0; row < 5; row++) {
    const tone = 90 + ((variant + row) % 4) * 18;
    fill(228, 112 + row * 53, width - 316, 13, [tone, tone + 7, tone + 17, 255]);
    fill(width - 72, 107 + row * 53, 30, 22, row === variant % 5 ? accent : [82, 91, 108, 255]);
  }
  fill(48, 112 + (variant % 5) * 53, 112, 13, accent);
  for (let y = 0; y < height; y++) raw[y * rowSize] = 0;

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

function pngChunk(type, data) {
  const typeBytes = Buffer.from(type, 'ascii');
  const chunk = Buffer.concat([typeBytes, data]);
  const output = Buffer.alloc(data.length + 12);
  output.writeUInt32BE(data.length, 0);
  chunk.copy(output, 4);
  output.writeUInt32BE(crc32(chunk), data.length + 8);
  return output;
}

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function writeHistory(dir, task, id, index) {
  const t0 = iso(index * 30 + 100);
  const t1 = iso(index * 30 + 104);
  const t2 = iso(index * 30 + 110);
  const sha = 'demo000000000000000000000000000000000000';

  writeJsonl(join(dir, 'logs', 'timeline.jsonl'), [
    { ts: t0, kind: 'prompt_created', actor: `human:${OWNER}`, payloadRef: 'prompt.md', summary: `Task created: ${task.title}`, details: { targetState: '0-backlog', agent: 'claude', provenance: PROVENANCE } },
    { ts: t1, kind: 'agent_run_started', actor: 'system', summary: 'claude CLI start', details: { cli: 'claude', intent: 'start', resumed: 'false', provenance: PROVENANCE } },
    { ts: t2, kind: 'agent_run_finished', actor: 'agent', summary: 'claude run finished in 354,2s', details: { cli: 'claude', status: 'completed', provenance: PROVENANCE } },
  ]);

  writeJsonl(join(dir, 'logs', 'session-events.jsonl'), [
    { Ts: t1, Kind: 'start', Cli: 'claude', InputSessionId: null, CapturedSessionId: `demo-session-${task.key.toLowerCase()}`, Resumed: false, Reason: null, HeadShaBefore: sha, HeadShaAfter: sha, ContextRef: 'logs/run-context/run-demo.md', Provenance: PROVENANCE },
  ]);

  writeJson(join(dir, 'pipeline-execution.json'), {
    provenance: PROVENANCE,
    pipelineId: 'standard-task-pipeline',
    pipelineVersion: 1,
    jobId: id,
    project: PROJECTS.find((p) => p.key === task.project).name,
    startedAt: t1,
    steps: [
      { stepId: 'core-agent-run', kind: 1, model: 'claude-opus-4-8', status: 2, startedAt: t1, completedAt: t2, durationMs: 354200, inputTokens: 42827, outputTokens: 30668, cacheReadTokens: 2284920, cacheCreationTokens: 341824 },
      { stepId: 'aspect-code-quality', kind: 2, model: 'claude-haiku-4-5', status: 2, startedAt: t2, completedAt: t2, durationMs: 8200, inputTokens: 1200, outputTokens: 540, cacheReadTokens: 18000, cacheCreationTokens: 0 },
    ],
  });
}

function writeDecisionJournal(root) {
  const task = TASKS.find((candidate) => candidate.key === DECISION.taskKey);
  if (!task) throw new Error(`Pinned decision task is missing: ${DECISION.taskKey}`);
  const id = slug(task);
  const reviewFileName = 'code-review-grade-2026-08-09T12-32-00Z.md';
  writeJsonl(join(root, 'logs', 'decisions', 'Demo App.jsonl'), [{
    createdAt: iso(273),
    jobId: id,
    project: 'Demo App',
    kind: 'Escalate',
    reason: '[review-loop-budget-exhausted] One focused assertion remains for the operator.',
    prompt: 'Review the attached Grade B verdict and choose the bounded release path.',
    response: 'The implementation is reviewable and one focused test gap remains. [[DECISION:ESCALATE]]',
    followUp: '',
    attemptChainId: 'demo-release-chain-1',
    gateId: 'demo-release-decision',
    subjectSha: 'd3e0c9f84a1b7e62551a7a51c3b84cf6d2a09871',
    failureFingerprint: 'empty-table-assertion',
    failureKind: 'review-loop-budget-exhausted',
    councilReaction: {
      createdAt: iso(273),
      reviewFileName,
      grade: 'B',
      disposition: 'Escalate',
      summary: `Escalate ${DECISION.reviewFindings.length} open review findings; loop budget exhausted.`,
      assessments: DECISION.reviewFindings.map((finding) => ({
        finding,
        action: 'Escalate',
        reason: 'The bounded automatic review budget is exhausted; the operator owns the release decision.'
      })),
      startsNewRound: false,
      targetJobId: null,
      targetRunAttempt: null
    },
    attemptEpoch: 0
  }]);
}

function writeWorkspaceRootFiles(root) {
  // Token history for the global statistics views.
  const usageRows = [];
  const sources = ['prompt-enhancement', 'title-generation', 'task-classification', 'summary-generation'];
  for (let i = 0; i < 8; i++) {
    usageRows.push({
      ts: iso(i * 45),
      source: sources[i % sources.length],
      model: 'claude-haiku-4-5',
      inputTokens: 9 + i,
      outputTokens: 500 + i * 23,
      cacheReadTokens: 27913,
      cacheCreationTokens: 6000 + i * 40,
      durationMs: 7000 + i * 300,
      ok: true,
    });
  }
  writeJsonl(join(root, 'adhoc-usage.jsonl'), usageRows);

  // Per-project runtime settings: keep the demo projects on manual so a dev
  // backend brought up for debugging never auto-runs the demo cards.
  const settings = {};
  for (const p of PROJECTS) {
    settings[p.name] = {
      AutoCommit: false,
      AutoPushStrategy: 'off',
      RunnerMode: 'manual',
      OrchestratorModel: null,
      OrchestratorThinkingLevel: null,
      AnalysisSchedules: null,
      AutonomyLevel: null,
      IntakeEnabled: null,
      LaneSortStrategyOverrides: null,
      PipelineSteps: null,
      MaxParallelism: 1,
      IntegrationBranch: 'develop',
      IntegrationStrategy: 'direct-merge',
      CliModes: null,
    };
  }
  writeJson(join(root, 'project-settings.json'), settings);

  writeJson(join(root, 'tags.json'), {
    Version: 1,
    Tags: [
      { Name: 'demo', Color: '#a78bfa' },
      { Name: 'frontend', Color: '#60a5fa' },
      { Name: 'platform', Color: '#34d399' },
    ],
  });

  writeText(
    join(root, 'README.md'),
    '# Demo TaskRepository (generated)\n\nSlim, reproducible datastore for the DEV backend (ADR-0056). Generated by\n`scripts/seed-demo-workspace.mjs` in the agent-taskboard repo. Do not hand-edit\nfor anything you want to keep — re-running the seed resets this store to a\nclean stand.\n'
  );
}

function writePinnedRepositoryMarker(projectRoot) {
  const gitDir = join(projectRoot, '.git');
  if (!existsSync(gitDir)) {
    mkdirSync(join(gitDir, 'objects'), { recursive: true });
    mkdirSync(join(gitDir, 'refs', 'heads'), { recursive: true });
    writeText(join(gitDir, 'HEAD'), 'ref: refs/heads/main\n');
    writeText(
      join(gitDir, 'config'),
      '[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = false\n'
    );
  }
  writeText(join(projectRoot, '.gitignore'), 'tasks/\n.orchestrator/\n');
}

// ---- Wiki trees and Dossier gallery ------------------------------------
//
// The Wiki of a project is the docs/ folder of its repository checkout, and a
// Dossier is any folder below docs/ that carries a workbench.json descriptor.
// Both trees are rendered from one pinned block model so a page and a document
// stay in the same voice and a re-seed stays byte-identical.

function wikiPage(project, relPath) {
  const tree = WIKI_TREES.find((candidate) => candidate.project === project);
  const page = tree?.pages.find((candidate) => candidate.path === relPath);
  if (!page) throw new Error(`Pinned wiki page is missing: ${project}/${relPath}`);
  return page;
}

function escapeHtml(text) {
  return text
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;');
}

/**
 * Inline markdown links and code spans, rendered into escaped HTML. A `task:`
 * link stays readable text: the Wiki reader resolves that scheme, but the
 * sandboxed Dossier host refuses every scheme-prefixed href, so rendering it as
 * an anchor there would produce a link that silently does nothing.
 */
function inlineHtml(text) {
  return escapeHtml(text)
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_match, label, href) =>
      href.startsWith('task:') ? `<code>${label}</code>` : `<a href="${href}">${label}</a>`)
    .replace(/`([^`]+)`/g, (_match, code) => `<code>${code}</code>`);
}

function markdownCell(value) {
  return value.replaceAll('|', '\\|');
}

function markdownTable(table) {
  const header = `| ${table.columns.map(markdownCell).join(' | ')} |`;
  const divider = `| ${table.columns.map(() => '---').join(' | ')} |`;
  const rows = table.rows.map((row) => `| ${row.map(markdownCell).join(' | ')} |`);
  return [header, divider, ...rows].join('\n');
}

function blocksToMarkdown(blocks) {
  const out = [];
  for (const block of blocks) {
    if (block.h !== undefined) out.push(`## ${block.h}`);
    else if (block.h3 !== undefined) out.push(`### ${block.h3}`);
    else if (block.p !== undefined) out.push(block.p);
    else if (block.ul !== undefined) out.push(block.ul.map((item) => `- ${item}`).join('\n'));
    else if (block.ol !== undefined) out.push(block.ol.map((item, index) => `${index + 1}. ${item}`).join('\n'));
    else if (block.table !== undefined) out.push(markdownTable(block.table));
    else if (block.note !== undefined) out.push(`> ${block.note}`);
    else if (block.decision !== undefined) {
      out.push(`### ${block.decision.question}`);
      out.push(block.decision.options.map((option) => `- **${option.label}**: ${option.summary}`).join('\n'));
    } else throw new Error(`Unsupported pinned block: ${JSON.stringify(block)}`);
  }
  return out.join('\n\n');
}

function decisionHtml(decision) {
  const options = decision.options
    .map((option) => `        <li data-option-id="${escapeHtml(option.id)}"><b>${escapeHtml(option.label)}</b>: ${inlineHtml(option.summary)}</li>`)
    .join('\n');
  return [
    `    <div class="callout" data-tone="accent" data-decision-id="${escapeHtml(decision.id)}" data-decision-kind="${escapeHtml(decision.kind)}">`,
    `      <h3>${inlineHtml(decision.question)}</h3>`,
    '      <ul>',
    options,
    '      </ul>',
    '      <label>Optional note',
    `        <textarea data-comment="${escapeHtml(decision.comment)}" rows="2"></textarea>`,
    '      </label>',
    '    </div>',
  ].join('\n');
}

function blockHtml(block) {
  if (block.h3 !== undefined) return `    <h3>${inlineHtml(block.h3)}</h3>`;
  if (block.p !== undefined) return `    <p>${inlineHtml(block.p)}</p>`;
  if (block.ul !== undefined)
    return `    <ul>\n${block.ul.map((item) => `      <li>${inlineHtml(item)}</li>`).join('\n')}\n    </ul>`;
  if (block.ol !== undefined)
    return `    <ol>\n${block.ol.map((item) => `      <li>${inlineHtml(item)}</li>`).join('\n')}\n    </ol>`;
  if (block.table !== undefined) return tableHtml(block.table);
  if (block.note !== undefined) return `    <p class="dim">${inlineHtml(block.note)}</p>`;
  if (block.decision !== undefined) return decisionHtml(block.decision);
  throw new Error(`Unsupported pinned block: ${JSON.stringify(block)}`);
}

/**
 * Renders the block list as numbered house-style sections: every h block opens
 * one section, and blocks before the first heading form the opening section.
 */
function blocksToHtml(blocks) {
  const sections = [];
  for (const block of blocks) {
    if (block.h !== undefined) sections.push({ title: block.h, body: [] });
    else if (sections.length === 0) sections.push({ title: null, body: [blockHtml(block)] });
    else sections[sections.length - 1].body.push(blockHtml(block));
  }

  let number = 0;
  return sections
    .map((section) => {
      const heading = section.title === null
        ? []
        : [`    <h2><span class="number">${pad(++number)}</span> ${inlineHtml(section.title)}</h2>`];
      return ['  <section>', ...heading, ...section.body, '  </section>'].join('\n');
    })
    .join('\n\n');
}

/** The number the References section gets after the pinned content sections. */
function sectionNumber(blocks) {
  return pad(blocks.filter((block) => block.h !== undefined).length + 1);
}

function pad(value) {
  return String(value).padStart(2, '0');
}

function tableHtml(table) {
  const head = table.columns.map((column) => `<th scope="col">${inlineHtml(column)}</th>`).join('');
  const body = table.rows
    .map((row) => `        <tr>${row.map((cell) => `<td>${inlineHtml(cell)}</td>`).join('')}</tr>`)
    .join('\n');
  return [
    '    <table>',
    `      <thead><tr>${head}</tr></thead>`,
    '      <tbody>',
    body,
    '      </tbody>',
    '    </table>',
  ].join('\n');
}

/** Quoted frontmatter scalar; the reader trims the quotes back off. */
function yamlScalar(value) {
  if (value.includes('"') || value.includes('\n')) {
    throw new Error(`Pinned frontmatter value must not contain a quote or newline: ${value}`);
  }
  return `"${value}"`;
}

function pageMarkdown(page) {
  const frontmatter = [
    '---',
    `title: ${yamlScalar(page.title)}`,
    `summary: ${yamlScalar(page.summary)}`,
    `last-updated: ${iso(page.offsetMinutes).slice(0, 10)}`,
    '---',
  ].join('\n');
  return `${frontmatter}\n\n# ${page.title}\n\n${blocksToMarkdown(page.blocks)}\n`;
}

/**
 * Page-to-card companions, in the reduced shape the product's own cross-
 * reference writer produces: a relatedTasks array and nothing else. The larger
 * grading companion schema is deliberately not used, because its mandatory
 * $schema constant is the one place a source-project string would reach the
 * generated datastore.
 */
function pageCompanions(project) {
  const companions = new Map();
  for (const [taskKey, pages] of Object.entries(CARD_WIKI_PAGES)) {
    const task = TASKS.find((candidate) => candidate.key === taskKey);
    if (!task || task.project !== project) continue;
    for (const relPath of pages) {
      const page = wikiPage(project, relPath);
      if (!companions.has(relPath)) companions.set(relPath, []);
      companions.get(relPath).push({
        key: task.key,
        title: task.title,
        linkedAt: iso(page.offsetMinutes),
        source: 'manual',
      });
    }
  }
  return companions;
}

function writeWikiTrees(root) {
  for (const tree of WIKI_TREES) {
    const docs = join(root, 'projects', tree.project, 'docs');
    const companions = pageCompanions(tree.project);
    for (const page of tree.pages) {
      writeText(join(docs, page.path), pageMarkdown(page));
      const relatedTasks = companions.get(page.path);
      if (relatedTasks) writeJson(join(docs, `${page.path}.meta.json`), { relatedTasks });
    }
    writeJson(join(docs, 'app', 'config', 'home.json'), tree.home);
  }
}

function dossierHtml(item) {
  const cards = [...item.sourceTaskKeys, ...item.relatedTaskKeys];
  const references = [
    ...cards.map((key) => {
      const task = TASKS.find((candidate) => candidate.key === key);
      return `      <li><code>${escapeHtml(key)}</code>: ${escapeHtml(task ? task.title : 'seeded card')}</li>`;
    }),
    ...item.links.map((link) =>
      `      <li><a href="${escapeHtml(link.href)}">${escapeHtml(link.label)}</a></li>`),
  ].join('\n');
  const storedState = item.schemaVersion === 2 ? item.lifecycleState : item.status;

  return `${articleHead(item)}
<main class="article">
  <header class="page">
    <p class="kicker">Pinned demo data | ${escapeHtml(item.key)}</p>
    <h1>${escapeHtml(item.title)}</h1>
    <p class="lede">${escapeHtml(item.summary)}</p>
    <div class="meta"><span><b>Stored state</b> ${escapeHtml(storedState)} / ${escapeHtml(item.phase)}</span><span><b>Shown as</b> ${escapeHtml(item.status)}</span><span><b>Edited by</b> ${escapeHtml(item.editedBy)}</span><span><b>Updated</b> ${isoSeconds(item.offsetMinutes)}</span></div>
  </header>

  <section>
    <p>${inlineHtml(item.lead)}</p>
  </section>

${blocksToHtml(item.blocks)}

  <section>
    <h2><span class="number">${sectionNumber(item.blocks)}</span> References</h2>
    <ul>
${references}
    </ul>
    <p class="dim">Pinned demo data. This document, its cards, and its measurements are invented for the demo instance. Decision points are fixtures and start no operation.</p>
  </section>
</main>
</body>
</html>
`;
}

/**
 * Head of the canonical article document, so a demo Dossier renders in the same
 * house style as a real one instead of inventing a second colour system.
 */
function articleHead(item) {
  const head = ARTICLE_TEMPLATE.slice(0, ARTICLE_TEMPLATE.indexOf('<main class="article">'))
    .replaceAll('{{title}}', escapeHtml(item.title))
    .replaceAll('{{summary}}', escapeHtml(item.summary))
    .replaceAll('{{pattern}}', 'concept')
    .replaceAll('{{status}}', escapeHtml(item.status))
    .replaceAll('{{phase}}', escapeHtml(item.phase))
    // The scaffold titles itself "<title> | Article document"; a Dossier in the
    // Wiki tree should read as its own title.
    .replace(`<title>${escapeHtml(item.title)} | Article document</title>`,
      `<title>${escapeHtml(item.title)}</title>`)
    .trimEnd();
  if (head.includes('{{')) throw new Error('The article template has an unfilled placeholder.');
  return head;
}

/**
 * Lifecycle-schema-aware descriptor. Schema 2 stores the lifecycle state, its
 * history, and the decision receipt; the retention Dossier stays on schema 1
 * because "decision-pending while an operator has not answered" has no receipt
 * to record, and a public demo must not carry a half-executed operation.
 */
function dossierDescriptor(item) {
  const common = {
    id: item.id,
    key: item.key,
    title: item.title,
    summary: item.summary,
    entrypoint: 'index.html',
    phase: item.phase,
    editedBy: item.editedBy,
  };
  if (item.schemaVersion !== 2) {
    return {
      schemaVersion: 1,
      ...common,
      status: item.status,
      updatedAt: isoSeconds(item.offsetMinutes),
      sourceTaskKeys: item.sourceTaskKeys,
      relatedTaskKeys: item.relatedTaskKeys,
      decision: null,
    };
  }

  const history = item.lifecycleHistory.map((entry) => ({
    state: entry.state,
    editedBy: entry.editedBy,
    editedAt: isoSeconds(entry.offsetMinutes),
    ...(entry.note ? { note: entry.note } : {}),
  }));
  const latest = history[history.length - 1];
  if (latest.state !== item.lifecycleState
    || latest.editedBy !== item.editedBy
    || latest.editedAt !== isoSeconds(item.offsetMinutes)) {
    throw new Error(`Pinned lifecycle history does not end at the current state: ${item.key}`);
  }

  return {
    schemaVersion: 2,
    pageKind: 'workbench',
    ...common,
    lifecycleState: item.lifecycleState,
    editedAt: isoSeconds(item.offsetMinutes),
    lifecycleHistory: history,
    sourceTaskKeys: item.sourceTaskKeys,
    relatedTaskKeys: item.relatedTaskKeys,
    decision: item.decision ? decisionReceipt(item.decision) : null,
  };
}

/** Pinned decision receipt: recorded provenance, never an in-flight operation. */
function decisionReceipt(decision) {
  const receipt = {
    outcome: decision.outcome,
    state: decision.state,
    operationId: decision.operationId,
    sourceRevision: decision.sourceRevision,
    preparedAt: isoSeconds(decision.preparedAt),
    preparedBy: decision.preparedBy,
    confirmedAt: isoSeconds(decision.confirmedAt),
    confirmedBy: decision.confirmedBy,
    decidedAt: isoSeconds(decision.decidedAt),
    spawnedTaskKeys: decision.spawnedTaskKeys,
    responses: decision.responses,
  };
  if (decision.outcome === 'archive') return { ...receipt, reason: decision.reason };
  return { ...receipt, taskDraft: decision.taskDraft };
}

function writeDossierGallery(root) {
  const galleryRoot = join(root, 'projects', DOSSIERS.project, 'docs', DOSSIERS.root);
  for (const item of DOSSIERS.items) {
    const dir = join(galleryRoot, item.id);
    writeText(join(dir, 'index.html'), dossierHtml(item));
    writeJson(join(dir, 'workbench.json'), dossierDescriptor(item));
  }
}

function writePresentationStory(root) {
  const demoApp = join(root, 'projects', 'demo-app');
  const demoPlatform = join(root, 'projects', 'demo-platform');
  writePinnedRepositoryMarker(demoApp);
  writePinnedRepositoryMarker(demoPlatform);
  writeText(
    join(demoApp, 'README.md'),
    '# Demo App\n\nA deterministic sample product used only for Agent Studio demonstrations.\n'
  );
  writeJsonl(join(demoApp, '.orchestrator', 'orchestrator-chat.jsonl'), [
    { id: 'demo-chat-01', ts: iso(210), role: 'user', text: 'What should we show in the MVP walkthrough?' },
    { id: 'demo-chat-02', ts: iso(211), role: 'orchestrator', text: 'Start with the cross-lane board, open DEMO-5 to connect execution with review evidence, then finish in project knowledge and token usage.' },
    { id: 'demo-chat-03', ts: iso(212), role: 'user', text: 'Keep the demo safe and repeatable.' },
    { id: 'demo-chat-04', ts: iso(213), role: 'orchestrator', text: 'Confirmed. This workspace contains seeded demo data only and can be reset before every capture.' },
  ]);
}

// ---- Idempotent reset + run --------------------------------------------

function reset(root) {
  // Remove only the paths this seed owns; leave an operator-added .git etc.
  for (const managed of ['projects', '.metadata', 'logs', 'adhoc-usage.jsonl', 'project-settings.json', 'tags.json', 'README.md']) {
    rmSync(join(root, managed), { recursive: true, force: true });
  }
}

function stampTree(root) {
  if (!existsSync(root)) return;
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) stampTree(path);
    stamp(path);
  }
  stamp(root);
}

function main() {
  const { root } = parseArgs(process.argv.slice(2));
  const productionGuard = root.replace(/[\\/]+$/, '').toLowerCase();
  if (productionGuard.endsWith('agent-taskboard-workspace')) {
    console.error(`Refusing to seed the production workspace: ${root}`);
    console.error('Pass a separate --root (e.g. ...\\agent-taskboard-workspace-demo).');
    process.exit(1);
  }

  mkdirSync(root, { recursive: true });
  reset(root);

  TASKS.forEach((task, i) => writeTask(root, task, i));
  writeWorkspaceRootFiles(root);
  writePresentationStory(root);
  writeWikiTrees(root);
  writeDossierGallery(root);
  writeDecisionJournal(root);
  stampTree(root);

  const perLane = TASKS.reduce((acc, t) => ((acc[t.state] = (acc[t.state] || 0) + 1), acc), {});
  const perStatus = DOSSIERS.items.reduce((acc, d) => ((acc[d.status] = (acc[d.status] || 0) + 1), acc), {});
  console.log(`Seeded demo store at: ${root}`);
  console.log(`  projects: ${PROJECTS.map((p) => p.name).join(', ')}`);
  console.log(`  tasks:    ${TASKS.length} (${TASKS.filter((t) => t.history).length} with run/token history)`);
  console.log(`  pinned:   ${DECISION.taskKey} carries review, diff, screenshot, evidence, and decision state`);
  console.log(`  lanes:    ${Object.keys(perLane).sort().join(', ')}`);
  console.log(`  wiki:     ${WIKI_TREES.map((tree) => `${tree.project} (${tree.pages.length} pages)`).join(', ')}`);
  console.log(`  dossiers: ${DOSSIERS.items.length} in ${DOSSIERS.project}/docs/${DOSSIERS.root} (${Object.keys(perStatus).sort().join(', ')})`);
  console.log('Registry (.metadata) is left to the backend to seed from WatchPaths on first boot (ADR-0042).');
}

const __isMain = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (__isMain) main();
