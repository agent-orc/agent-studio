// Run: node --test scripts/migration/move-studio-to-server.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, utimes, writeFile } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  buildInventory, classifyPath, classifyWorkspace, credentialChecklist, generateMapping,
  readProjectRegistry, runCli, scanWorkspace, selectDelta,
} from './move-studio-to-server.mjs';

const fixture = JSON.parse(readFileSync(new URL('./fixtures/move-studio-workspace.json', import.meta.url), 'utf8'));
const fixedClock = () => new Date('2026-10-19T08:30:00.000Z');
// Credential-shaped files are created at runtime so no such file lands in the repository.
const credentialFiles = {
  '.git-credentials': ['https://operator', 'fake-' + 'value@example.invalid'].join(':') + '\n',
  'projects/demo/.codex/auth.json': JSON.stringify({ token: 'fake-' + 'runtime' }),
  'notes/deploy.pem': 'not a real key',
};

async function materialize(files) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-studio-'));
  const workspace = path.join(root, 'workspace');
  for (const [relative, content] of Object.entries(files)) {
    const file = path.join(workspace, ...relative.split('/'));
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, content);
  }
  await mkdir(path.join(workspace, '.metadata'), { recursive: true });
  await writeFile(path.join(workspace, '.metadata', 'projects.json'), JSON.stringify(fixture.registry, null, 2));
  await mkdir(path.join(workspace, '.git', 'objects'), { recursive: true });
  await writeFile(path.join(workspace, '.git', 'HEAD'), 'ref: refs/heads/main\n');
  await mkdir(path.join(workspace, 'node_modules', 'pkg'), { recursive: true });
  await writeFile(path.join(workspace, 'node_modules', 'pkg', 'index.js'), '');
  return { root, workspace };
}

test('classifies workspace areas and Git state into transfer routes', () => {
  const cases = [
    ['.metadata/attempt-authority.json', {}, 'attempt-authority', 'untracked', 'archive'],
    ['.metadata/attempt-authority.archive-2026-10.json', {}, 'attempt-authority', 'untracked', 'archive'],
    ['.metadata/projects.json', { tracked: true }, 'metadata', 'tracked', 'git'],
    ['.security/access-state.json', {}, 'access-state', 'untracked', 'archive'],
    ['.runtime/cli-output/AGT-1.log', {}, 'runtime', 'untracked', 'archive'],
    ['.orchestration-ai-asks/ask-1.json', {}, 'orchestration-state', 'untracked', 'archive'],
    ['logs/bus/2026-10-18.jsonl', {}, 'bus-log', 'untracked', 'archive'],
    ['logs/api.log', {}, 'logs', 'untracked', 'archive'],
    ['projects/demo/tasks/3-done/AGT-1/results/report.html', {}, 'results', 'untracked', 'archive'],
    ['projects/demo/tasks/3-done/AGT-1/attachments/screenshot.png', {}, 'attachments', 'untracked', 'archive'],
    ['projects/demo/tasks/3-done/AGT-1/results', {}, 'other', 'untracked', 'archive'],
    ['projects/demo/tasks/3-done/AGT-1/status.md', { tracked: true, dirty: true }, 'other', 'tracked-modified', 'archive'],
    ['projects/demo/tasks/3-done/AGT-2/task.md', { tracked: true, present: false }, 'other', 'tracked-deleted', 'remove'],
    ['projects/demo/tasks/3-done/AGT-1/write.json.tmp', {}, 'other', 'untracked', 'exclude'],
    ['.git-credentials', {}, 'other', 'untracked', 'exclude'],
    ['projects/demo/.codex/auth.json', {}, 'other', 'untracked', 'exclude'],
    ['config/.env.production', { tracked: true, dirty: true }, 'other', 'tracked-modified', 'exclude'],
    ['config/.env.example', {}, 'other', 'untracked', 'archive'],
    ['config/server.pem', { tracked: true }, 'other', 'tracked', 'git'],
  ];
  for (const [relative, git, area, state, transfer] of cases) {
    const result = classifyPath(relative, git);
    assert.deepEqual([result.area, result.state, result.transfer], [area, state, transfer], relative);
  }
  assert.equal(classifyPath('.git-credentials').reason, 'credential');
  assert.match(classifyPath('config/server.pem', { tracked: true }).reason, /already in Git history/);
});

test('scans the fixture workspace without Git internals and joins tracked and dirty lists', async () => {
  const { root, workspace } = await materialize({ ...fixture.files, ...credentialFiles });
  try {
    const files = scanWorkspace(workspace);
    assert.equal(files.some(file => file.path.startsWith('.git/') || file.path.includes('node_modules')), false);
    assert.equal(files.every(file => !file.path.includes('\\')), true);
    const entries = classifyWorkspace({ files, tracked: fixture.tracked, dirty: fixture.dirty });
    const byPath = Object.fromEntries(entries.map(entry => [entry.path, entry]));
    assert.equal(byPath['projects/demo/tasks/3-done/AGT-1/task.md'].transfer, 'git');
    assert.equal(byPath['projects/demo/tasks/3-done/AGT-1/status.md'].state, 'tracked-modified');
    assert.equal(byPath['projects/demo/tasks/3-done/AGT-2/task.md'].transfer, 'remove');
    assert.equal(byPath['notes/scratch.md'].transfer, 'archive');
    assert.deepEqual(entries.filter(entry => entry.reason === 'credential').map(entry => entry.path).sort(),
      ['.git-credentials', 'notes/deploy.pem', 'projects/demo/.codex/auth.json']);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('reads the registry from the backup when the primary file is invalid', async () => {
  const { root, workspace } = await materialize({});
  try {
    const primary = path.join(workspace, '.metadata', 'projects.json');
    await writeFile(primary + '.bak', await readFile(primary, 'utf8'));
    await writeFile(primary, '{ truncated');
    const projects = readProjectRegistry(workspace);
    assert.deepEqual(projects.map(project => project.id), ['PROJ-001', 'PROJ-002', 'PROJ-003', 'PROJ-004', 'PROJ-005']);
    assert.equal(projects[4].archived, true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('generates workspace and checkout mappings with stable unique names', () => {
  const projects = readRegistryFromFixture();
  const plan = generateMapping({ workspace: fixture.workspace, projects, remotes: fixture.remotes });
  assert.deepEqual(plan.mapping, {
    'C:\\Studio\\agent-taskboard-workspace': '/data/workspace',
    'D:\\Code\\Demo': '/data/projects/demo',
    'E:\\Other\\Demo': '/data/projects/demo-2',
    'F:\\Legacy': '/data/projects/legacy',
  });
  assert.deepEqual(plan.checkouts.map(item => [item.name, item.repositoryUrl, item.projects]), [
    ['demo', 'git@github.com:example/demo.git', ['PROJ-001', 'PROJ-002']],
    ['demo-2', 'https://github.com/example/demo-two.git', ['PROJ-003']],
    ['legacy', null, ['PROJ-005']],
  ]);
  assert.deepEqual(plan.issues.map(issue => [issue.code, issue.projects.join(','), issue.path]), [
    ['missing-repository-url', 'PROJ-005', 'F:\\Legacy'],
    ['unmapped-path', 'PROJ-005', 'G:\\Elsewhere\\legacy-root'],
  ]);
});

test('extra mapping and an origin remote resolve every blocking issue', () => {
  const projects = readRegistryFromFixture();
  const plan = generateMapping({
    workspace: fixture.workspace,
    projects,
    remotes: { ...fixture.remotes, 'PROJ-005': 'ssh://git@example.invalid/legacy.git', 'PROJ-001': 'not a remote' },
    extra: { 'G:\\Elsewhere\\legacy-root': '/data/projects/legacy/root' },
  });
  assert.deepEqual(plan.issues, []);
  assert.equal(plan.mapping['G:\\Elsewhere\\legacy-root'], '/data/projects/legacy/root');
  assert.equal(plan.checkouts.find(item => item.name === 'demo').repositoryUrl, 'git@github.com:example/demo.git');
  assert.throws(() => generateMapping({ workspace: fixture.workspace, projects, extra: { 'relative': '/x' } }), /Invalid extra mapping/);
});

test('reports conflicting repository URLs for one shared checkout', () => {
  const projects = readRegistryFromFixture().slice(0, 2);
  const plan = generateMapping({ workspace: fixture.workspace, projects, remotes: { 'PROJ-002': 'https://github.com/example/fork.git' } });
  assert.deepEqual(plan.issues.map(issue => issue.code), ['conflicting-repository-urls']);
});

test('credential checklist names a replacement for every login it does not copy', () => {
  const home = fixture.home;
  const present = new Set([path.join(home, '.codex/auth.json'), path.join(home, '.ssh/id_ed25519')]);
  const items = credentialChecklist({ home, exists: file => present.has(file), credentialHelper: 'manager' });
  const byId = Object.fromEntries(items.map(item => [item.id, item]));
  assert.equal(byId.codex.present, true);
  assert.equal(byId.claude.present, false);
  assert.equal(byId['ssh-keys'].present, true);
  assert.equal(byId['git-credential-manager'].present, true);
  assert.equal(items.every(item => item.replacement.length > 20), true);
  assert.equal(credentialChecklist({ home, exists: () => false, credentialHelper: 'store' })
    .find(item => item.id === 'git-credential-manager').present, false);
});

test('delta selection sends new and changed files once and removes vanished ones', () => {
  const entry = (file, size, mtimeMs, extra = {}) => ({ path: file, size, mtimeMs, transfer: 'archive', state: 'untracked', ...extra });
  const first = selectDelta({ entries: [
    entry('.runtime/a.log', 10, 1000),
    entry('logs/bus/b.jsonl', 5, 1000),
    entry('projects/demo/status.md', 3, 1000, { state: 'tracked-modified' }),
    { path: 'projects/demo/task.md', size: 3, mtimeMs: 1000, transfer: 'git', state: 'tracked' },
    { path: '.git-credentials', size: 3, mtimeMs: 1000, transfer: 'exclude', state: 'untracked' },
  ] });
  assert.deepEqual(first.copy, ['.runtime/a.log', 'logs/bus/b.jsonl', 'projects/demo/status.md']);
  assert.deepEqual(first.remove, []);

  const unchanged = selectDelta({ entries: [
    entry('.runtime/a.log', 10, 1000),
    entry('logs/bus/b.jsonl', 5, 1000),
    entry('projects/demo/status.md', 3, 1000, { state: 'tracked-modified' }),
  ], previous: first.manifest });
  assert.deepEqual(unchanged.copy, ['projects/demo/status.md']);
  assert.deepEqual(unchanged.remove, []);
  assert.equal(unchanged.unchanged, 2);

  const second = selectDelta({ entries: [
    entry('.runtime/a.log', 10, 2000),
    entry('logs/bus/c.jsonl', 1, 2000),
    { path: 'projects/demo/status.md', size: 3, mtimeMs: 1000, transfer: 'git', state: 'tracked' },
    { path: 'projects/demo/gone.md', size: 0, mtimeMs: 0, transfer: 'remove', state: 'tracked-deleted' },
  ], previous: first.manifest });
  assert.deepEqual(second.copy, ['.runtime/a.log', 'logs/bus/c.jsonl']);
  assert.deepEqual(second.remove, ['logs/bus/b.jsonl', 'projects/demo/gone.md']);
  assert.deepEqual(Object.keys(second.manifest.files), ['.runtime/a.log', 'logs/bus/c.jsonl']);
});

test('delta selection refuses paths that could escape the server workspace', () => {
  for (const unsafe of ['../outside', '/etc/passwd', 'C:\\x', 'a//b', 'a/../../b', 'line\nbreak']) {
    assert.throws(() => selectDelta({ entries: [{ path: unsafe, size: 1, mtimeMs: 1, transfer: 'archive', state: 'untracked' }] }), /Unsafe/, unsafe);
    assert.throws(() => selectDelta({ entries: [], previous: { files: { [unsafe]: { size: 1, mtimeMs: 1 } } } }), /Unsafe/, unsafe);
  }
});

test('inventory carries the injected clock and the CLI writes plan files and a repeatable delta', async () => {
  const { root, workspace } = await materialize({ ...fixture.files, ...credentialFiles });
  try {
    const inventory = buildInventory({
      workspace: fixture.workspace,
      files: scanWorkspace(workspace),
      tracked: fixture.tracked,
      dirty: fixture.dirty,
      projects: readProjectRegistry(workspace),
      remotes: fixture.remotes,
      extra: {},
      home: fixture.home,
      exists: () => false,
      credentialHelper: '',
      now: fixedClock,
    });
    assert.equal(inventory.generatedAt, '2026-10-19T08:30:00.000Z');
    assert.equal(inventory.summary.areas['attempt-authority'].untracked, 2);
    assert.equal(inventory.summary.areas.metadata.tracked, 2);
    assert.equal(inventory.summary.totals.remove, 1);

    const state = path.join(root, 'state');
    await mkdir(state);
    await writeFile(path.join(state, 'tracked.z'), fixture.tracked.map(item => item + '\0').join(''));
    await writeFile(path.join(state, 'dirty.z'), fixture.dirty.map(item => item + '\0').join(''));
    await writeFile(path.join(state, 'remotes.tsv'), 'PROJ-003\thttps://github.com/example/demo-two.git\nPROJ-005\thttps://github.com/example/legacy.git\n');
    await writeFile(path.join(state, 'extra.json'), JSON.stringify({ 'G:\\Elsewhere\\legacy-root': '/data/projects/legacy/root' }));
    const output = [];
    const write = text => output.push(text);
    const run = args => runCli(args, { now: fixedClock, write });
    assert.equal(run(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace, '--tracked', path.join(state, 'tracked.z'), '--dirty', path.join(state, 'dirty.z'),
      '--remotes', path.join(state, 'remotes.tsv'), '--extra-mapping', path.join(state, 'extra.json'), '--home', fixture.home, '--out-dir', state]), 0);
    assert.match(output.join(''), /not copied \(credential\): \.git-credentials/);
    const checkouts = await readFile(path.join(state, 'checkouts.tsv'), 'utf8');
    assert.equal(checkouts.split('\n').filter(Boolean).length, 3);

    const deltaArgs = ['delta', '--inventory', path.join(state, 'inventory.json'), '--copy', path.join(state, 'copy.z'),
      '--remove', path.join(state, 'remove.z'), '--manifest', path.join(state, 'manifest.json')];
    assert.equal(run(deltaArgs), 0);
    const firstCopy = (await readFile(path.join(state, 'copy.z'), 'utf8')).split('\0').filter(Boolean);
    assert.equal(firstCopy.includes('.security/access-state.json'), true);
    assert.equal(firstCopy.includes('.git-credentials'), false);
    assert.equal(firstCopy.includes('projects/demo/tasks/3-done/AGT-1/write.json.tmp'), false);
    assert.equal(JSON.parse(await readFile(path.join(state, 'manifest.json'), 'utf8')).generatedAt, '2026-10-19T08:30:00.000Z');

    const later = new Date('2026-10-19T09:00:00.000Z');
    await utimes(path.join(workspace, 'logs', 'bus', '2026-10-18.jsonl'), later, later);
    assert.equal(run(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace, '--tracked', path.join(state, 'tracked.z'), '--dirty', path.join(state, 'dirty.z'),
      '--remotes', path.join(state, 'remotes.tsv'), '--extra-mapping', path.join(state, 'extra.json'), '--home', fixture.home, '--out-dir', state]), 0);
    assert.equal(run([...deltaArgs.slice(0, 7), '--previous', path.join(state, 'manifest.json'), '--manifest', path.join(state, 'manifest.next.json')]), 0);
    assert.deepEqual((await readFile(path.join(state, 'copy.z'), 'utf8')).split('\0').filter(Boolean),
      ['logs/bus/2026-10-18.jsonl', 'projects/demo/tasks/3-done/AGT-1/status.md']);
    assert.deepEqual((await readFile(path.join(state, 'remove.z'), 'utf8')).split('\0').filter(Boolean),
      ['projects/demo/tasks/3-done/AGT-2/task.md']);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('inventory exits 3 while a blocking mapping issue remains', async () => {
  const { root, workspace } = await materialize({});
  try {
    const status = runCli(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace, '--home', fixture.home, '--out-dir', root], { now: fixedClock, write: () => {} });
    assert.equal(status, 3);
    const inventory = JSON.parse(await readFile(path.join(root, 'inventory.json'), 'utf8'));
    assert.deepEqual(inventory.issues.map(issue => [issue.code, issue.projects.join(',')]),
      [['missing-repository-url', 'PROJ-003'], ['missing-repository-url', 'PROJ-005'], ['unmapped-path', 'PROJ-005']]);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

function readRegistryFromFixture() {
  return fixture.registry.Projects.map(project => ({
    id: project.Id,
    displayName: project.DisplayName,
    storageLocation: project.StorageLocation,
    repositoryPath: project.RepositoryPath,
    rootPath: project.RootPath,
    archived: project.Archived === true,
    urls: project.Urls.map(url => ({ id: url.Id, label: url.Label, url: url.Url })),
  }));
}
