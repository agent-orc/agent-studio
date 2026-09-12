#!/usr/bin/env node

import { execFileSync } from 'node:child_process';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const output = 'docs/operations/doku-inventur-2026-07/README.md';
const roots = ['docs/concepts', 'docs/operations', 'docs/research'];

const archived = new Map([
  ['docs/concepts/companion-app-design.md', 'Moved to docs/archive/concepts; reference implementation only.'],
  ['docs/concepts/docs-structure-migration.md', 'Moved to docs/archive/concepts; current contract is system/contracts/wiki-tree.md.'],
  ['docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md', 'Moved to docs/archive/concepts; implementation has shipped.'],
  ['docs/concepts/project-chat-progress-indicator-2026-06-08.md', 'Moved to docs/archive/concepts; predates multichat.'],
  ['docs/concepts/zielstruktur-wiki.html', 'Moved to docs/archive/concepts; overtaken by the 19 July tree migration.'],
]);

const partial = new Map([
  ['docs/concepts/drei-einheiten-architektur.html', 'Runner invocation and extraction snapshot are stale; role boundary remains current.'],
  ['docs/concepts/orchestrator-chat-redesign-handoff.md', 'Former-panel analysis is stale; product direction remains useful.'],
  ['docs/concepts/parallel-task-execution.md', 'Premise and single-slot sections predate delivered maxParallelism.'],
  ['docs/concepts/planning-research-task-kinds-2026-05.md', 'Future-tense mode and promotion sections have shipped.'],
  ['docs/concepts/planning-research-task-type.html', 'Gap analysis and build plan partly shipped on 29 July.'],
  ['docs/concepts/solution-workspace-component-project-model.md', 'Migration-state claims predate current registry; target model remains active.'],
  ['docs/concepts/task-integration-merge-config-analysis.html', 'Current-state analysis predates transactional acceptance and target-branch truth.'],
  ['docs/operations/model-routing-policy/index.html', 'Historical Workbench rationale; canonical policy supersedes current-state sections.'],
  ['docs/operations/wiki-archiv-konzept/index.html', 'Proposed _archiv path and resolver are not implemented; Phase 1 uses docs/archive plus pointers.'],
]);

const evidence = new Map([
  ['docs/concepts/companion-app-design.md', 'V06: Companion code exists; AGENTS product boundary says reference only.'],
  ['docs/concepts/docs-structure-migration.md', 'V01: wiki-tree contract and ProjectDocsService checked.'],
  ['docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md', 'V02: TaskScanner phase model and lane E2E specs checked.'],
  ['docs/concepts/project-chat-progress-indicator-2026-06-08.md', 'V05: current side-sheet, multichat commits, and routes checked.'],
  ['docs/concepts/zielstruktur-wiki.html', 'V01: 19 July migration and physical-tree contract checked.'],
  ['docs/concepts/drei-einheiten-architektur.html', 'V08: RunSpec and current runner-host runbook checked.'],
  ['docs/concepts/orchestrator-chat-redesign-handoff.md', 'V05: current multichat implementation checked.'],
  ['docs/concepts/parallel-task-execution.md', 'V03: ProjectSettings and multi-slot runner history checked.'],
  ['docs/concepts/planning-research-task-kinds-2026-05.md', 'V04: TaskModes, read-only pipeline, promotion API checked.'],
  ['docs/concepts/planning-research-task-type.html', 'V04: 29 July planning spawn and decision deliveries checked.'],
  ['docs/concepts/solution-workspace-component-project-model.md', 'V14: registry code and Tasks domain map checked.'],
  ['docs/concepts/task-integration-merge-config-analysis.html', 'V07: 29 July integration commits and current workflow checked.'],
  ['docs/operations/model-routing-policy/index.html', 'V09: canonical model-routing policy and qualification code checked.'],
  ['docs/operations/wiki-archiv-konzept/index.html', 'V01: archive proposal checked against current wiki contract and Phase 1 decision.'],
]);

function walk(path) {
  if (!existsSync(path)) return [];
  const result = [];
  for (const name of readdirSync(path).sort()) {
    const full = join(path, name);
    const info = statSync(full);
    if (info.isDirectory()) result.push(...walk(full));
    else if (/\.(md|html?|htm)$/i.test(name)) result.push(full);
  }
  return result;
}

function git(args) {
  return execFileSync('git', args, {
    cwd: root,
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    windowsHide: true,
  });
}

function historyIndex() {
  const raw = git([
    'log',
    '--date=short',
    '--format=@@@%H%x09%ad%x09%s',
    '--name-only',
    '--',
    ...roots,
  ]);
  const index = new Map();
  let commit = null;
  for (const line of raw.split(/\r?\n/)) {
    if (line.startsWith('@@@')) {
      const [sha, date, ...subject] = line.slice(3).split('\t');
      commit = { sha, date, subject: subject.join('\t') };
      continue;
    }
    const path = line.trim();
    if (!path || !commit || !/\.(md|html?|htm)$/i.test(path)) continue;
    const item = index.get(path) ?? { latest: commit, commits: 0 };
    item.commits += 1;
    index.set(path, item);
  }
  return index;
}

function plain(value) {
  return value
    .replace(/<[^>]+>/g, ' ')
    .replace(/&amp;/g, '&')
    .replace(/&nbsp;/g, ' ')
    .replace(/&mdash;|&ndash;/g, '-')
    .replace(/[—–]/g, '-')
    .replace(/&#\d+;/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function title(path) {
  const text = readFileSync(join(root, path), 'utf8');
  if (path.endsWith('.md')) {
    const match = text.match(/^#\s+(.+)$/m);
    if (match) return plain(match[1].replace(/[`*_]/g, ''));
  }
  const h1 = text.match(/<h1[^>]*>([\s\S]*?)<\/h1>/i);
  if (h1) return plain(h1[1]);
  const htmlTitle = text.match(/<title[^>]*>([\s\S]*?)<\/title>/i);
  return htmlTitle ? plain(htmlTitle[1]) : path.split('/').at(-1);
}

function topic(path) {
  const parts = path.split('/');
  if (parts[2] === 'common-problems') {
    return `Recurring problem: ${parts[3]} (${parts.at(-1).replace(/\.(md|html?)$/i, '')})`;
  }
  if (parts[2] === 'proposals') {
    return `Survey proposal: ${title(path)}`;
  }
  if (parts.length > 4) {
    return `${parts.slice(2, -1).join(' / ')}: ${title(path)}`;
  }
  return title(path);
}

function verdict(path) {
  if (archived.has(path)) return { kind: 'outdated / archived', note: archived.get(path) };
  if (partial.has(path)) return { kind: 'partial', note: partial.get(path) };
  if (path.includes('/proposals/')) {
    return { kind: 'current', note: 'Dated live proposal record; current status is frontmatter, not prose age.' };
  }
  if (path.includes('/common-problems/')) {
    return { kind: 'current', note: 'Current incident-pattern record; fixed entries remain valid history.' };
  }
  if (path.includes('/mockups/') || path.endsWith('.report.html')) {
    return { kind: 'current', note: 'Current as a dated reference artifact; not treated as an implementation contract.' };
  }
  if (path.includes('/designated-topics/')) {
    return { kind: 'current', note: 'Generated current-state pointer; producer owns refresh.' };
  }
  if (path === 'docs/operations/learnings/README.md') {
    return { kind: 'current', note: 'Generated index; producer owns refresh.' };
  }
  return { kind: 'current', note: 'No sampled contradiction found; temporal or target scope is preserved.' };
}

function verification(path) {
  if (evidence.has(path)) return evidence.get(path);
  if (path.includes('/proposals/')) return 'V12: ProjectProposalService loader and frontmatter contract sampled.';
  if (path.includes('/common-problems/')) return 'V11: status frontmatter and generated-index contract sampled.';
  if (path.includes('/setup/')) return 'V08: setup paths sampled against deploy files, endpoints, and runner options.';
  if (path.includes('/haertung-verteilte-ausfuehrung/')) return 'V10: attempt authority, runner, and Workbench contracts sampled.';
  if (path.includes('/review-pipeline-health')) return 'V07: current gate and review code sampled.';
  if (path.includes('/visual-style-guide')) return 'V15: reference status sampled against current frontend/style rules.';
  if (path.includes('/mockups/')) return 'Reference artifact: links and declared date checked; no live-code claim inferred.';
  if (path.endsWith('.report.html')) return 'Generated companion artifact: source relationship checked.';
  return 'Inventory read plus thematic code/link sample; no per-line implementation guarantee.';
}

function mdCell(value) {
  return String(value).replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
}

const files = roots
  .flatMap(path => walk(join(root, path)))
  .map(path => relative(root, path).replaceAll('\\', '/'))
  .filter(path => path !== output)
  .sort();
const history = historyIndex();
const rows = files.map(path => {
  const item = history.get(path);
  const standing = item
    ? `${item.latest.date}, \`${item.latest.sha.slice(0, 10)}\`, ${item.commits} path commit${item.commits === 1 ? '' : 's'}`
    : 'uncommitted inventory pointer; predecessor history retained at archive path';
  return { path, topic: topic(path), standing, ...verdict(path), verification: verification(path) };
});

const counts = rows.reduce((acc, row) => {
  acc[row.kind] = (acc[row.kind] ?? 0) + 1;
  return acc;
}, {});
const conceptCount = rows.filter(row => row.path.startsWith('docs/concepts/')).length;
const operationsCount = rows.filter(row => row.path.startsWith('docs/operations/')).length;
const researchCount = rows.filter(row => row.path.startsWith('docs/research/')).length;

const sections = [
  '# Documentation Inventory, July 2026',
  '',
  'Status: Phase 1 inventory and cleanup completed on 2026-07-29.',
  '',
  'This is the reviewable inventory for every readable Markdown or HTML page under',
  '`docs/concepts/`, `docs/operations/`, and `docs/research/` at the start of the',
  'cleanup. JSON descriptors, classification companions, schemas, and evidence',
  'payloads are provenance for their page and are not counted as separate knowledge',
  'documents. This generated inventory page excludes itself.',
  '',
  '## Outcome',
  '',
  `- ${rows.length} readable documents inventoried: ${conceptCount} concepts, ${operationsCount} operations, ${researchCount} research.`,
  `- ${counts.current ?? 0} current, ${counts.partial ?? 0} partial, ${counts['outdated / archived'] ?? 0} outdated and archived.`,
  `- ${counts['outdated / archived'] ?? 0} superseded pages moved to \`docs/archive/concepts/\`; their former paths now explain the archive and point to the current source.`,
  `- ${counts.partial ?? 0} partial pages carry a visible banner naming the stale sections, the change reference, and the current document.`,
  '- No registered Workbench was newly archived. The decided Workbenches are already quiet History entries. The registered model-routing Workbench is partial, remains decided, and now points to the canonical policy.',
  '- `docs/research/` does not exist in the current tree. Research pages were folded into dated concept pages during the 19 July migration; the inventory records this as a zero-page area rather than guessing at deleted content.',
  '',
  '## Method',
  '',
  '1. Enumerate every `.md`, `.html`, and `.htm` page in scope.',
  '2. Record its first heading, latest touching commit, commit count for the current path, declared status, and page family.',
  '3. Inspect the heading, declared state, and relevant claim sections of every top-level hand-maintained page, then use grouped review for uniform proposal, common-problem, generated, and mockup families.',
  '4. Check claims against current code, tests, schemas, domain maps, and recent delivery commits. The checks are samples, not an assertion that every line of every page was re-proved.',
  '5. Archive only where a replacement or explicit product boundary makes the whole page historical. Mark mixed pages visibly instead of silently rewriting their historical analysis.',
  '',
  '## Code and system-state samples',
  '',
  '| ID | Sample | Evidence and conclusion |',
  '|---|---|---|',
  '| V01 | Wiki tree and archive semantics | `docs/system/contracts/wiki-tree.md`, `ProjectDocsService`, and the 19 July migration were checked. The old eight-folder target is historical. Non-Workbench archive pages now live under `docs/archive/` with pointers. |',
  '| V02 | Lifecycle lanes and phases | `TaskScannerService`, `TaskInfo.phase`, `LifecyclePhaseCompatibilityTests`, and board lane E2E specs expose the shipped human-ready, intake, execution, post-processing, and review projection. The May “not implemented” plan is historical. |',
  '| V03 | Intra-project parallelism | `ProjectSettings.MaxParallelism`, capacity admission, and multi-slot runner history exist. The May concept section claiming one slot is stale; the current workflow and domain map are authoritative. |',
  '| V04 | Planning and research | `TaskModes`, `ReadOnlyContainmentPolicy`, `PipelineCatalogue.ReadOnlyPipeline`, planning promotion, spawn ledger, and 29 July integration commits were checked. Older future-tense sections are partial. |',
  '| V05 | Orchestrator chat | MC-0 through MC-4 commits and the current side-sheet/context code supersede the June blocking-request measurement and parts of the redesign handoff. |',
  '| V06 | Companion App | Relay and backend sync code still exist, but the root product boundary declares the Companion App reference code only. The page was archived so implementation existence is not mistaken for current capability. |',
  '| V07 | Integration and review | Commits `d1649ce92` and `7852330d0`, current workflow docs, integration services, and tests establish target-branch truth and transactional acceptance. The older configuration analysis is partial. |',
  '| V08 | Distributed setup | Runner options, RunSpec, deploy assets, `networked-task-server.md`, `multi-machine.md`, and the 28 July private Task Server proposal were sampled. Current and interim runbooks remain operationally distinct. |',
  '| V09 | Model routing | `docs/system/domains/model-routing-policy.md` is authoritative. `ModelQualificationService` and runner call sites exist. The decided Workbench remains rationale, but its “today” text and model ladder are historical. |',
  '| V10 | Workbench History | `WorkbenchCatalogueService` maps schema-v2 lifecycle `done` to archived History. The user wording `lifecycleState: archived` is not valid in the current schema; no descriptor was corrupted to satisfy the older wording. |',
  '| V11 | Common problems | README status frontmatter and the generated maintenance index contract were sampled. Open, mitigated, and recently fixed entries remain current operational knowledge, not cleanup debris. |',
  '| V12 | Proposals | `ProjectProposalService` reads the dated Markdown records directly. Their frontmatter is live application state, so age alone is not a reason to move them out of `concepts/proposals/`. |',
  '| V13 | Research area | The physical folder is absent. Commit `e2802d02b` records the five-folder migration; dated research now lives with concepts. |',
  '| V14 | Workspace/project model | Current registry and Tasks domain code were sampled. The target component-project model remains useful, but its migration snapshot and retired predecessor link were marked partial. |',
  '| V15 | Visual references | Visual Style Guide pages and mockups were treated as dated reference artifacts and sampled against current style hard rules. They were not promoted to live implementation contracts. |',
  '',
  '## Archive actions',
  '',
  '| Former page | Archive location | Current source |',
  '|---|---|---|',
  '| `concepts/docs-structure-migration.md` | `archive/concepts/docs-structure-migration.md` | `system/contracts/wiki-tree.md` |',
  '| `concepts/expanded-lifecycle-lanes-plan-2026-05.md` | `archive/concepts/expanded-lifecycle-lanes-plan-2026-05.md` | `system/domains/tasks.md` |',
  '| `concepts/project-chat-progress-indicator-2026-06-08.md` | `archive/concepts/project-chat-progress-indicator-2026-06-08.md` | `concepts/orchestrator-chat.md`, `concepts/multichat-orchestrator.md` |',
  '| `concepts/companion-app-design.md` | `archive/concepts/companion-app-design.md` | Root product boundary: reference code only |',
  '| `concepts/zielstruktur-wiki.html` | `archive/concepts/zielstruktur-wiki.html` | `system/contracts/wiki-tree.md` and this inventory |',
  '',
  '## Phase 2 sketch for Robert',
  '',
  'Phase 2 should make a structure decision only after this cleanup is accepted.',
  'The proposed decision package is:',
  '',
  '1. Keep three reader questions as the top-level rule: “How does it work now?” in `system/`, “Why or where is it going?” in `concepts/`, and “How do I operate it?” in `operations/`.',
  '2. Keep dated evidence close to its owning concept unless it is a generated report family. Do not recreate a broad `research/` dumping ground without a named owner and retention rule.',
  '3. Use lowercase kebab-case paths, date suffixes only for snapshots, and one canonical current page per topic. Historical predecessors always carry an archive pointer or a partial banner.',
  '4. Treat Workbenches as decision artifacts with lifecycle History, not as a second documentation taxonomy. A decided Workbench may stay discoverable without claiming current contract authority.',
  '5. Make `docs/start/README.md` the curated entry, domain maps the current-state authority, and `docs/archive/README.md` the sole archive explanation.',
  '',
  'Decision requested from Robert in the follow-up card: confirm this three-question',
  'taxonomy and whether dated evidence should remain beside concepts or move into one',
  'narrow `evidence/` family. No structure move is performed in Phase 1 beyond explicit',
  'archiving.',
  '',
  '## Per-document inventory',
  '',
  'Verdict meanings: **current** means correct for its declared role (including a',
  'dated reference or live data record); **partial** means named sections are stale;',
  '**outdated / archived** means the full former page has moved to the archive.',
  '',
];

for (const [label, prefix] of [
  ['Concepts', 'docs/concepts/'],
  ['Operations', 'docs/operations/'],
  ['Research', 'docs/research/'],
]) {
  sections.push(`### ${label}`, '');
  const areaRows = rows.filter(row => row.path.startsWith(prefix));
  if (areaRows.length === 0) {
    sections.push('_No readable documents in the current physical tree._', '');
    continue;
  }
  sections.push(
    '| Path | Topic | Stand | Verdict | Verification / qualification |',
    '|---|---|---|---|---|',
  );
  for (const row of areaRows) {
    sections.push(`| \`${mdCell(row.path)}\` | ${mdCell(row.topic)} | ${mdCell(row.standing)} | **${row.kind}**: ${mdCell(row.note)} | ${mdCell(row.verification)} |`);
  }
  sections.push('');
}

mkdirSync(dirname(join(root, output)), { recursive: true });
writeFileSync(join(root, output), `${sections.join('\n')}\n`, 'utf8');
console.log(`Wrote ${output} with ${rows.length} rows.`);
