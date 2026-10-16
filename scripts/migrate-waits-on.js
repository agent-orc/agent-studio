// AGT-2029 migration: convert the free-text "WARTET AUF: ..." notes on the
// current cards into real, structured waits-on dependencies.
//
// "Waits-on" is the existing F34 `references.dependsOn` relation given scheduler
// teeth by AGT-2029: a 2-ready card whose dependsOn targets have not reached
// 6-completed / 7-archive is held back from auto-pickup and shows an amber
// "waits: KEY" chip on the board (see docs/system/contracts/filesystem.md and the tasks
// domain map). This script sets those edges through the Task API - never by
// editing task.json directly (AGENTS.md: API-first).
//
// It is DRY-RUN by default: it prints the plan and changes nothing. Review the
// plan, then re-run with --apply to write. It is idempotent - re-running after
// an --apply is a no-op - and it preserves the other reference kinds
// (relatedTo / blockedBy / supersedes) because the PUT is replace-all.
//
// Usage:
//   node scripts/migrate-waits-on.js                 # dry run against stable (5031)
//   node scripts/migrate-waits-on.js --apply         # write the edges
//   TASKBOARD_PORT=5030 node scripts/migrate-waits-on.js   # target the dev backend
//
// This must run against a LIVE backend that hosts the real workspace; it cannot
// be run from a task worktree (the dev backend is offline by default - see
// AGENTS.md "Runtime And Stable").

import { taskApiRequest } from '../.agents/skills/task-api/scripts/access.js';

const HOST = '127.0.0.1';
const PORT = Number(process.env.TASKBOARD_PORT ?? 5031);
const APPLY = process.argv.includes('--apply');

// The dependency chains from the AGT-2029 directive. Sources and targets are
// matched by F33 key (preferred, stable) or by a title substring (fallback for
// cards whose key you do not know offhand). Each source card ends up waiting on
// the resolved KEYS of its targets. Verify the printed plan before --apply and
// adjust these matchers if a card is matched wrongly or missed.
const EDGES = [
  // CAR-3 (Pricing-Lib) -> AGT-Kosten-Audit -> WEB-Update
  { source: { titleIncludes: 'Kosten-Audit' }, waitsOn: [{ keyEquals: 'CAR-3' }] },
  { source: { titleIncludes: 'WEB-Update' }, waitsOn: [{ titleIncludes: 'Kosten-Audit' }] },
  // CAR-2 (ultra-Leiter) -> AGT-2025 (Modell-Defaults)
  { source: { keyEquals: 'AGT-2025' }, waitsOn: [{ keyEquals: 'CAR-2' }] },
  // The CAC-2 family: uncomment + set the real target once confirmed with the
  // operator. Left out by default so the migration never invents an edge.
  // { source: { keyEquals: 'CAC-2' }, waitsOn: [{ keyEquals: 'CAR-2' }] },
];

function request(method, path, body) {
  return new Promise((resolve, reject) => {
    const payload = body ? JSON.stringify(body) : null;
    const req = taskApiRequest(
      {
        hostname: HOST,
        port: PORT,
        path,
        method,
        headers: {
          'Content-Type': 'application/json',
          'X-Client-Id': 'local-default',
          ...(payload ? { 'Content-Length': Buffer.byteLength(payload) } : {}),
        },
      },
      (res) => {
        let d = '';
        res.on('data', (c) => (d += c));
        res.on('end', () => resolve({ status: res.statusCode, body: d }));
      },
    );
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

async function getJson(path) {
  const res = await request('GET', path);
  if (res.status !== 200) throw new Error(`GET ${path} -> ${res.status}: ${res.body.slice(0, 200)}`);
  return JSON.parse(res.body);
}

// Flatten /api/tasks/grouped into a single list of tasks (all lanes).
async function loadAllTasks() {
  const grouped = await getJson('/api/tasks/grouped');
  const tasks = [];
  for (const lane of Object.values(grouped)) if (Array.isArray(lane)) tasks.push(...lane);
  return tasks;
}

function matches(task, m) {
  if (m.keyEquals) return (task.key ?? '').toUpperCase() === m.keyEquals.toUpperCase();
  if (m.titleIncludes) return (task.title ?? '').toLowerCase().includes(m.titleIncludes.toLowerCase());
  return false;
}

function findOne(tasks, m, role) {
  const found = tasks.filter((t) => matches(t, m));
  if (found.length === 0) {
    console.warn(`  ! ${role} matcher ${JSON.stringify(m)} matched no task`);
    return null;
  }
  if (found.length > 1) {
    console.warn(
      `  ! ${role} matcher ${JSON.stringify(m)} matched ${found.length} tasks: ${found
        .map((t) => t.key ?? t.id)
        .join(', ')} - using the first; tighten the matcher`,
    );
  }
  return found[0];
}

function dependencyKey(edge) {
  return (typeof edge === 'string' ? edge : edge?.key ?? '').trim();
}

function sameSet(a, b) {
  const norm = (xs) => [...new Set(xs.map(dependencyKey).filter(Boolean).map((x) => x.toUpperCase()))].sort();
  const na = norm(a);
  const nb = norm(b);
  return na.length === nb.length && na.every((x, i) => x === nb[i]);
}

async function main() {
  console.log(`AGT-2029 waits-on migration ${APPLY ? '(APPLY)' : '(dry run)'} -> ${HOST}:${PORT}\n`);
  const tasks = await loadAllTasks();
  console.log(`Loaded ${tasks.length} tasks.\n`);

  let planned = 0;
  let applied = 0;
  let skipped = 0;

  for (const edge of EDGES) {
    const source = findOne(tasks, edge.source, 'source');
    if (!source) continue;

    const targetKeys = [];
    for (const tm of edge.waitsOn) {
      const target = findOne(tasks, tm, 'target');
      if (!target) continue;
      if (!target.key) {
        console.warn(`  ! target ${target.id} has no F33 key yet - skipping this edge`);
        continue;
      }
      targetKeys.push(target.key);
    }
    if (targetKeys.length === 0) continue;

    const current = source.references ?? { dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [] };
    const existing = current.dependsOn ?? [];
    // Union by key so we never drop or weaken a release-gated dependency the
    // operator already set by hand. New migration edges retain the legacy
    // string shape.
    const merged = [...existing];
    const known = new Set(existing.map(dependencyKey).filter(Boolean).map((key) => key.toUpperCase()));
    for (const key of targetKeys.map((value) => value.trim()).filter(Boolean)) {
      if (known.has(key.toUpperCase())) continue;
      merged.push(key);
      known.add(key.toUpperCase());
    }

    const label = `${source.key ?? source.id} "${source.title}"`;
    if (sameSet(existing, merged)) {
      console.log(`= ${label} already waits on ${merged.map(dependencyKey).join(', ')} - skip`);
      skipped++;
      continue;
    }

    console.log(`~ ${label}: dependsOn ${existing.map(dependencyKey).join(', ') || '(none)'}  ->  ${merged.map(dependencyKey).join(', ')}`);
    planned++;

    if (APPLY) {
      const nextRefs = {
        dependsOn: merged,
        relatedTo: current.relatedTo ?? [],
        blockedBy: current.blockedBy ?? [],
        supersedes: current.supersedes ?? [],
      };
      const path = `/api/tasks/${encodeURIComponent(source.id)}/references?watchPath=${encodeURIComponent(source.watchPath)}`;
      const res = await request('PUT', path, nextRefs);
      if (res.status === 200) {
        applied++;
        try {
          const warnings = JSON.parse(res.body).warnings ?? [];
          for (const w of warnings) console.warn(`    warn: ${w.message}`);
        } catch {
          /* body was the plain references object on an older backend */
        }
        console.log(`    applied`);
      } else {
        console.error(`    FAILED ${res.status}: ${res.body.slice(0, 200)}`);
      }
    }
  }

  console.log(
    `\nDone. planned=${planned} applied=${applied} skipped(idempotent)=${skipped}` +
      (APPLY ? '' : '  (dry run - re-run with --apply to write)'),
  );
}

main().catch((e) => {
  console.error('migration failed:', e.message);
  process.exit(1);
});
