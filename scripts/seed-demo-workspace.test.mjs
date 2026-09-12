// Acceptance coverage for the pinned demo seed (public demo instance, slice S1).
//
// The seed is the only content source of a publicly browsable instance, so the
// three properties proven here are the ones a release gate depends on:
// byte-identical generation, discovery across every lane and every Dossier
// lifecycle state, and a visitor surface that carries no source identity.

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import test, { after } from 'node:test';

const SEED = fileURLToPath(new URL('./seed-demo-workspace.mjs', import.meta.url));
const SNAPSHOT = JSON.parse(
  readFileSync(fileURLToPath(new URL('./presentation-capture/pinned-seed.json', import.meta.url)), 'utf8'));
const CONTENT = JSON.parse(
  readFileSync(fileURLToPath(new URL('./presentation-capture/pinned-demo-content.json', import.meta.url)), 'utf8'));

// Mirrored from the descriptor contract in
// backend/Features/Docs/WorkbenchCatalogueService.cs. A JS test cannot import
// the C# allowlists, so a change there has to land here in the same review.
const ALLOWED_STATUSES = ['active', 'decision-pending', 'decided', 'documented', 'archived'];
const ALLOWED_PHASES = ['informational', 'shaping', 'testing', 'decision-ready'];
const ALLOWED_LIFECYCLE_STATES = ['in-progress', 'review-requested', 'decided', 'documented', 'done'];
const TERMINAL_LANES = ['6-completed', '7-archive'];
const UTC_STAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/;

/** The status the catalogue projects for a descriptor, per schema version. */
function projectedStatus(descriptor) {
  if (descriptor.schemaVersion === 1) return descriptor.status;
  if (descriptor.lifecycleState === 'documented') return 'documented';
  if (!descriptor.decision) {
    return descriptor.lifecycleState === 'done' ? 'archived'
      : descriptor.lifecycleState === 'decided' ? 'decided' : 'active';
  }
  if (descriptor.decision.state !== 'succeeded') return 'decision-pending';
  return descriptor.decision.outcome === 'archive' ? 'archived' : 'decided';
}

/** Ids of the decision points the host can offer, per the counting contract. */
function decisionPoints(html) {
  return [...html.matchAll(/<[^>]*data-decision-id="([A-Za-z0-9_-]{1,80})"[^>]*>/g)]
    .filter((match) => /data-decision-kind="(single|multi|confirm)"/.test(match[0]))
    .map((match) => match[1]);
}

function assertReceipt(descriptor) {
  const receipt = descriptor.decision;
  assert.ok(['feature-spawn', 'archive'].includes(receipt.outcome));
  assert.ok(['pending', 'failed', 'succeeded'].includes(receipt.state));
  assert.match(receipt.operationId, /^[A-Za-z0-9._-]{8,128}$/);
  assert.match(receipt.sourceRevision, /^[a-f0-9]{7,64}$/);
  for (const stamp of [receipt.preparedAt, receipt.confirmedAt, receipt.decidedAt]) {
    assert.match(stamp, UTC_STAMP);
  }
  assert.ok(receipt.preparedBy && receipt.confirmedBy);
  for (const response of receipt.responses) {
    assert.ok(decisionPoints(
      readFileSync(descriptor.entryFullPath, 'utf8')).includes(response.decisionId),
      `receipt answers a decision point the document does not contain: ${response.decisionId}`);
    assert.equal(response.selectedOptionIds.length, 1);
  }
  if (receipt.outcome === 'archive') {
    assert.ok(receipt.reason, 'an archive receipt records why');
    assert.ok(!('taskDraft' in receipt));
    assert.deepEqual(receipt.spawnedTaskKeys, []);
  } else {
    assert.ok(!('reason' in receipt));
    assert.ok(receipt.taskDraft.title && receipt.taskDraft.goal);
    assert.ok(receipt.taskDraft.acceptanceCriteria.length > 0);
    assert.ok(['0-backlog', '1-preparation'].includes(receipt.taskDraft.initialLane));
    assert.ok(['coding', 'planning', 'research', 'concept'].includes(receipt.taskDraft.mode));
    assert.ok(['bug', 'feature', 'chore'].includes(receipt.taskDraft.taskType));
  }
}

function seedInto(root) {
  execFileSync(process.execPath, [SEED, '--root', root], { stdio: 'pipe', windowsHide: true });
  return root;
}

function walk(root, base = root, out = []) {
  for (const entry of readdirSync(root, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
    const path = join(root, entry.name);
    if (entry.isDirectory()) walk(path, base, out);
    else out.push(relative(base, path).split('\\').join('/'));
  }
  return out;
}

/**
 * One generation is shared by the read-only tests; only the determinism test
 * needs a second, independent root.
 */
let sharedRoot = null;
function withSeed(fn, roots = 1) {
  if (roots === 1) {
    sharedRoot ??= seedInto(mkdtempSync(join(tmpdir(), 'demo-seed-')));
    fn(sharedRoot);
    return;
  }
  const created = [];
  try {
    for (let i = 0; i < roots; i++) created.push(seedInto(mkdtempSync(join(tmpdir(), 'demo-seed-'))));
    fn(...created);
  } finally {
    for (const root of created) rmSync(root, { recursive: true, force: true });
  }
}

after(() => {
  if (sharedRoot) rmSync(sharedRoot, { recursive: true, force: true });
});

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

function taskFolder(root, task) {
  return join(root, 'projects', task.project, 'tasks', '000', task.key);
}

test('two independent generations are byte-identical', () => {
  withSeed((first, second) => {
    const firstFiles = walk(first);
    assert.deepEqual(walk(second), firstFiles);
    assert.ok(firstFiles.length > 100, 'the seed writes a substantial datastore');
    for (const rel of firstFiles) {
      assert.deepEqual(
        readFileSync(join(second, rel)),
        readFileSync(join(first, rel)),
        `generated bytes differ for ${rel}`);
    }
  }, 2);
});

test('every board lane is discoverable on disk', () => {
  withSeed((root) => {
    const lanes = new Set();
    for (const task of SNAPSHOT.tasks) {
      const stored = readJson(join(taskFolder(root, task), 'task.json'));
      assert.equal(stored.key, task.key);
      assert.equal(stored.state, task.state);
      lanes.add(stored.state);
    }
    assert.deepEqual([...lanes].sort(), [
      '0-backlog', '1-preparation', '2-ready', '3-progress', '4-auto-review',
      '5-human-review', '5e-escalated', '6-completed', '7-archive',
    ]);
  });
});

test('the gallery covers every Dossier lifecycle state with valid descriptors', () => {
  withSeed((root) => {
    const galleryRoot = join(root, 'projects', CONTENT.dossiers.project, 'docs', CONTENT.dossiers.root);
    const seenKeys = new Set();
    const seenStatuses = new Set();

    for (const item of CONTENT.dossiers.items) {
      const folder = join(galleryRoot, item.id);
      const descriptor = readJson(join(folder, 'workbench.json'));

      assert.ok([1, 2].includes(descriptor.schemaVersion), `schemaVersion ${descriptor.schemaVersion}`);
      assert.equal(descriptor.id, item.id, 'id must match the containing folder');
      assert.match(descriptor.id, /^[A-Za-z0-9_-]{1,80}$/);
      assert.match(descriptor.key, /^DEMO-W[1-9][0-9]*$/);
      assert.ok(!seenKeys.has(descriptor.key), `duplicate document key ${descriptor.key}`);
      seenKeys.add(descriptor.key);
      assert.ok(descriptor.title && descriptor.summary);
      assert.ok(ALLOWED_PHASES.includes(descriptor.phase), `phase ${descriptor.phase}`);
      assert.equal(descriptor.entrypoint, 'index.html');
      assert.ok(statSync(join(folder, descriptor.entrypoint)).size > 0);
      seenStatuses.add(projectedStatus(descriptor));

      if (descriptor.schemaVersion === 1) {
        assert.ok(ALLOWED_STATUSES.includes(descriptor.status), `status ${descriptor.status}`);
        assert.match(descriptor.updatedAt, UTC_STAMP);
        assert.equal(descriptor.decision, null, 'a schema-1 demo descriptor stores no receipt');
      } else {
        assert.equal(descriptor.pageKind, 'workbench');
        assert.ok(!('status' in descriptor) && !('updatedAt' in descriptor),
          'schema 2 must not store the legacy status or updatedAt fields');
        assert.ok(ALLOWED_LIFECYCLE_STATES.includes(descriptor.lifecycleState),
          `lifecycleState ${descriptor.lifecycleState}`);
        assert.match(descriptor.editedAt, UTC_STAMP);
        assert.ok(descriptor.lifecycleHistory.length > 0, 'lifecycleHistory needs at least one entry');
        for (const entry of descriptor.lifecycleHistory) {
          assert.ok(ALLOWED_LIFECYCLE_STATES.includes(entry.state), `history state ${entry.state}`);
          assert.match(entry.editedAt, UTC_STAMP);
          assert.ok(entry.editedBy);
        }
        const latest = descriptor.lifecycleHistory.at(-1);
        assert.equal(latest.state, descriptor.lifecycleState);
        assert.equal(latest.editedBy, descriptor.editedBy);
        assert.equal(latest.editedAt, descriptor.editedAt);
        if (descriptor.lifecycleState === 'decided' || descriptor.lifecycleState === 'done') {
          assert.ok(descriptor.decision, 'a settled lifecycle state needs its decision receipt');
        }
      }

      if (descriptor.decision) {
        assertReceipt({ ...descriptor, entryFullPath: join(folder, descriptor.entrypoint) });
      }

      const html = readFileSync(join(folder, 'index.html'), 'utf8');
      assert.match(html, /^<!doctype html>/);
      assert.match(html, /Pinned demo data/);
      assert.match(html, /<style data-article-template="v2">/,
        'a demo Dossier renders in the canonical article house style');
      assert.doesNotMatch(html, /<script/i, 'a pinned document ships no executable snippet');
      assert.doesNotMatch(html, /(src|href)="https?:/i, 'a pinned document embeds no remote content');
      // The sandboxed host refuses scheme-prefixed hrefs and anything that
      // leaves docs/, so a link the viewer cannot follow must not be rendered.
      for (const [, href] of html.matchAll(/href="([^"]+)"/g)) {
        assert.doesNotMatch(href, /^[a-z][a-z0-9+.-]*:/i, `unfollowable link ${href}`);
        if (href.startsWith('#')) continue;
        const target = join(CONTENT.dossiers.root, item.id, href);
        assert.ok(!relative('.', target).startsWith('..'), `link leaves the Wiki: ${href}`);
        assert.ok(statSync(join(root, 'projects', CONTENT.dossiers.project, 'docs', target)).isFile(),
          `missing link target ${href}`);
      }
    }

    assert.deepEqual([...seenStatuses].sort(), [...ALLOWED_STATUSES].sort(),
      'all five projected lifecycle states are seeded');
  });
});

test('Dossier references resolve to seeded cards in both directions', () => {
  withSeed((root) => {
    const byKey = new Map(SNAPSHOT.tasks.map((task) => [task.key, task]));
    const galleryRoot = join(root, 'projects', CONTENT.dossiers.project, 'docs', CONTENT.dossiers.root);

    // Card side: references.workbenches is the canonical edge.
    const cardsByWorkbench = new Map();
    for (const task of SNAPSHOT.tasks) {
      const stored = readJson(join(taskFolder(root, task), 'task.json'));
      for (const key of stored.references?.workbenches ?? []) {
        if (!cardsByWorkbench.has(key)) cardsByWorkbench.set(key, []);
        cardsByWorkbench.get(key).push(task);
      }
    }

    for (const item of CONTENT.dossiers.items) {
      const descriptor = readJson(join(galleryRoot, item.id, 'workbench.json'));
      for (const key of [...descriptor.sourceTaskKeys, ...descriptor.relatedTaskKeys]) {
        assert.ok(byKey.has(key), `${descriptor.key} references unseeded card ${key}`);
      }
      const cards = cardsByWorkbench.get(descriptor.key) ?? [];
      assert.ok(cards.length > 0, `${descriptor.key} has no card carrying the canonical edge`);
    }

    // "In implementation" is a projection, not a stored state: a decided
    // Dossier with started, non-terminal cards must exist for it to appear.
    const tracking = cardsByWorkbench.get('DEMO-W4') ?? [];
    assert.ok(tracking.some((task) => !TERMINAL_LANES.includes(task.state)));
    assert.ok(tracking.some((task) => TERMINAL_LANES.includes(task.state)));

    // A documented Dossier is only eligible when every reference is terminal.
    const documented = CONTENT.dossiers.items.find((item) => item.status === 'documented');
    assert.ok(documented, 'the gallery has a documented Dossier');
    const documentedRefs = [...documented.sourceTaskKeys, ...documented.relatedTaskKeys];
    assert.ok(documentedRefs.length > 0);
    for (const key of documentedRefs) {
      assert.ok(TERMINAL_LANES.includes(byKey.get(key).state), `${key} must be terminal`);
    }
  });
});

test('the decision-pending Dossier carries answerable decision points', () => {
  withSeed((root) => {
    const pending = CONTENT.dossiers.items.filter((item) => item.status === 'decision-pending');
    assert.ok(pending.length > 0);
    for (const item of pending) {
      const html = readFileSync(
        join(root, 'projects', CONTENT.dossiers.project, 'docs', CONTENT.dossiers.root, item.id, 'index.html'),
        'utf8');
      const points = decisionPoints(html);
      assert.equal(points.length, 2, 'the retention Dossier leaves two decisions open');
      assert.equal(new Set(points).size, points.length, 'decision ids are unique');
      assert.ok([...html.matchAll(/data-option-id="[A-Za-z0-9_-]{1,80}"/g)].length >= 2 * points.length,
        'every open decision offers at least two options');
    }
  });
});

test('both Wiki trees are filed, linked, and labelled as pinned demo data', () => {
  withSeed((root) => {
    const taskKeys = new Set(SNAPSHOT.tasks.map((task) => task.key));
    for (const tree of CONTENT.wiki) {
      const docs = join(root, 'projects', tree.project, 'docs');
      const pages = walk(docs).filter((rel) => rel.endsWith('.md'));
      assert.deepEqual(pages.sort(), tree.pages.map((page) => page.path).sort());

      for (const rel of pages) {
        const text = readFileSync(join(docs, rel), 'utf8');
        assert.match(text, /^---\ntitle: /, `${rel} needs pinned frontmatter`);
        assert.match(text, /Pinned demo data/, `${rel} must label itself as pinned demo data`);
        // A page at the Wiki root that is not a landing file shows up as
        // unfiled in Pulse, which reads as an unfinished workspace.
        assert.ok(rel.includes('/') || rel === 'README.md', `${rel} is loose at the Wiki root`);

        for (const [, , target] of text.matchAll(/\[([^\]]+)\]\(([^)\s]+)\)/g)) {
          if (target.startsWith('task:')) {
            assert.ok(taskKeys.has(target.slice(5)), `${rel} links unseeded card ${target}`);
            continue;
          }
          const resolved = join(docs, rel, '..', target);
          assert.ok(!relative(docs, resolved).startsWith('..'),
            `${rel} links outside its own Wiki: ${target}`);
          assert.ok(statSync(resolved).isFile(), `${rel} links missing page ${target}`);
        }
      }

      const home = readJson(join(docs, 'app', 'config', 'home.json'));
      for (const section of home.sections) {
        for (const link of section.links) {
          assert.ok(statSync(join(docs, link.relPath)).isFile(), `home.json links missing page ${link.relPath}`);
        }
      }
    }
  });
});

test('card and page cross-references point at each other', () => {
  withSeed((root) => {
    const byKey = new Map(SNAPSHOT.tasks.map((task) => [task.key, task]));
    for (const [taskKey, pages] of Object.entries(CONTENT.cardWikiPages)) {
      const task = byKey.get(taskKey);
      assert.ok(task, `cardWikiPages names unseeded card ${taskKey}`);
      const docs = join(root, 'projects', task.project, 'docs');
      const stored = readJson(join(taskFolder(root, task), 'task.json'));
      const storedPaths = stored.relatedWikiPages.map((page) => page.relPath);

      for (const relPath of pages) {
        // Repository-relative, as the product's own writer stores it: the
        // scanner resolves existence against the repository root, so a
        // docs-relative value would render every chip as a dead reference.
        assert.ok(storedPaths.includes(`docs/${relPath}`),
          `${taskKey} must reference docs/${relPath}`);
        assert.ok(statSync(join(root, 'projects', task.project, 'docs', relPath)).isFile());

        const companion = readJson(join(docs, `${relPath}.meta.json`));
        assert.ok(companion.relatedTasks.some((entry) => entry.key === taskKey),
          `${relPath} must link back to ${taskKey}`);
        assert.deepEqual(Object.keys(companion), ['relatedTasks'],
          'the seeded companion stays in the reduced cross-reference shape');
      }
    }
  });
});

test('every recorded execution declares itself as replayed', () => {
  withSeed((root) => {
    let checked = 0;
    for (const task of SNAPSHOT.tasks.filter((candidate) => candidate.history)) {
      const dir = taskFolder(root, task);
      for (const line of readFileSync(join(dir, 'logs', 'timeline.jsonl'), 'utf8').trim().split('\n')) {
        const event = JSON.parse(line);
        assert.equal(event.details?.provenance, 'pinned-demo-simulated',
          `${task.key} timeline event ${event.kind} has no provenance marker`);
        checked++;
      }
      assert.equal(readJson(join(dir, 'pipeline-execution.json')).provenance, 'pinned-demo-simulated');
    }
    assert.ok(checked > 10, 'the seeded scene carries recorded runs');
  });
});

test('the visitor surface carries no source identity', () => {
  withSeed((root) => {
    // The data boundary of the public instance: everything a visitor can reach
    // lives under projects/. Invented content only, no reconstructed source.
    const forbidden = [
      [/agent-taskboard/i, 'source repository name'],
      [/\bAGT-\d+\b/, 'source task key'],
      [/\bADR-\d{4}\b/, 'source decision id'],
      [/[A-Za-z]:\\{1,2}(Users|Projects)/i, 'absolute Windows path'],
      [/\/(home|Users)\/[a-z]/i, 'user home path'],
      [/https?:\/\//i, 'remote URL'],
      [/\b\d{1,3}(\.\d{1,3}){3}\b/, 'IP address'],
      [/[A-Za-z0-9._-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/, 'email address'],
      [/\bgit@|\.git\b(?!ignore)/, 'repository remote'],
    ];
    const projects = join(root, 'projects');
    for (const rel of walk(projects)) {
      if (rel.endsWith('.png') || rel.includes('/.git/')) continue;
      const text = readFileSync(join(projects, rel), 'utf8');
      for (const [pattern, label] of forbidden) {
        assert.doesNotMatch(text, pattern, `${rel} leaks a ${label}`);
      }
    }
  });
});
