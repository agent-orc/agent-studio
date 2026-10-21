// Run: node --test scripts/migration/move-studio-to-server.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, symlink, utimes, writeFile } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  buildInventory, classifyPath, classifyWorkspace, credentialChecklist, fileTokenPattern, findTokenPattern, generateMapping,
  readProjectRegistry, runCli, scanWorkspace, screenTokens, selectDelta, stripUrlCredentials, taskRoots, tokenStateIssues,
} from './move-studio-to-server.mjs';
import { relocate } from './relocate-workspace-paths.mjs';

const fixture = JSON.parse(readFileSync(new URL('./fixtures/move-studio-workspace.json', import.meta.url), 'utf8'));
const fixedClock = () => new Date('2026-10-19T08:30:00.000Z');
// Token-shaped values are assembled at runtime so no provider-shaped literal lands in the repository.
const fakeToken = {
  github: () => ['gh', 'p_', 'x'.repeat(36)].join(''),
  githubPat: () => ['github', '_pat_', 'y'.repeat(40)].join(''),
  anthropic: () => ['sk', '-ant-', 'z'.repeat(40)].join(''),
  openai: () => ['sk', '-proj-', 'w'.repeat(40)].join(''),
  aws: () => ['AK', 'IA', 'Q'.repeat(16)].join(''),
  google: () => ['AI', 'za', 'v'.repeat(35)].join(''),
  slack: () => ['xo', 'xb-', '1'.repeat(12)].join(''),
  npm: () => ['np', 'm_', 'u'.repeat(36)].join(''),
  privateKey: () => ['-----BEGIN', 'OPENSSH', 'PRIVATE', 'KEY-----'].join(' '),
};
// Credential-shaped files are created at runtime so no such file lands in the repository.
const credentialFiles = {
  '.git-credentials': ['https://operator', 'fake-' + 'value@example.invalid'].join(':') + '\n',
  'projects/demo/.codex/auth.json': JSON.stringify({ token: 'fake-' + 'runtime' }),
  'notes/deploy.pem': 'not a real key',
  'runner.env': 'RUNNER_TOKEN=fake-' + 'runtime\n',
  '.runtime/cli-output/AGT-9.log': `pushing with ${fakeToken.github()}\n`,
};

async function materialize(files) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-studio-'));
  const workspace = path.join(root, 'workspace');
  const registry = structuredClone(fixture.registry);
  const storage = path.join(root, 'external', '.orchestrator', 'jobs');
  registry.Projects.find(project => project.Id === 'PROJ-005').StorageLocation = storage;
  await mkdir(path.join(storage, 'AGT-1'), { recursive: true });
  await writeFile(path.join(storage, 'AGT-1', 'task.md'), '# external task\n');
  for (const [relative, content] of Object.entries(files)) {
    const file = path.join(workspace, ...relative.split('/'));
    await mkdir(path.dirname(file), { recursive: true });
    await writeFile(file, content);
  }
  await mkdir(path.join(workspace, '.metadata'), { recursive: true });
  await writeFile(path.join(workspace, '.metadata', 'projects.json'), JSON.stringify(registry, null, 2));
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
    ['runner.env', {}, 'other', 'untracked', 'exclude'],
    ['home/.remote-execution.env', {}, 'other', 'untracked', 'exclude'],
    ['secrets/ci.token', {}, 'other', 'untracked', 'exclude'],
    ['.npmrc', {}, 'other', 'untracked', 'exclude'],
    ['home/.config/gh/hosts.yml', {}, 'other', 'untracked', 'exclude'],
    ['home/AppData/Roaming/GitHub CLI/hosts.yml', {}, 'other', 'untracked', 'exclude'],
    ['home/.config/github-copilot/apps.json', {}, 'other', 'untracked', 'exclude'],
    ['home/.docker/config.json', {}, 'other', 'untracked', 'exclude'],
    ['docs/hosts.yml', {}, 'other', 'untracked', 'archive'],
    ['projects/demo/config.json', {}, 'other', 'untracked', 'archive'],
    ['runner.env.template', {}, 'other', 'untracked', 'archive'],
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
      ['.git-credentials', 'notes/deploy.pem', 'projects/demo/.codex/auth.json', 'runner.env']);
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
    'F:\\Legacy\\.orchestrator\\jobs': '/data/projects/.task-storage/PROJ-005',
  });
  assert.deepEqual(plan.storageRoots, [{ id: 'PROJ-005', source: 'F:\\Legacy\\.orchestrator\\jobs',
    target: '/data/projects/.task-storage/PROJ-005' }]);
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
  assert.equal(plan.mapping['F:\\Legacy\\.orchestrator\\jobs'], '/data/projects/.task-storage/PROJ-005');
  assert.equal(plan.checkouts.find(item => item.name === 'demo').repositoryUrl, 'git@github.com:example/demo.git');
  assert.throws(() => generateMapping({ workspace: fixture.workspace, projects, extra: { 'relative': '/x' } }), /Invalid extra mapping/);
});

test('task storage under a checkout or outside it gets its own copied root', () => {
  const projects = [
    { id: 'PROJ-011', storageLocation: 'C:\\Projects\\repo\\.orchestrator\\jobs', repositoryPath: 'C:\\Projects\\repo', urls: [] },
    { id: 'PROJ-014', storageLocation: 'D:\\Tasks\\jobs', repositoryPath: null, rootPath: null, urls: [] },
  ];
  const plan = generateMapping({ workspace: 'C:\\Projects\\workspace', projects,
    remotes: { 'PROJ-011': 'git@example.invalid:repo.git' },
    extra: { 'D:\\Tasks': '/data/projects/incorrect' } });
  assert.deepEqual(plan.storageRoots.map(root => root.target),
    ['/data/projects/.task-storage/PROJ-011', '/data/projects/.task-storage/PROJ-014']);
  assert.equal(plan.mapping['C:\\Projects\\repo\\.orchestrator\\jobs'], plan.storageRoots[0].target);
  assert.equal(plan.mapping['D:\\Tasks\\jobs'], plan.storageRoots[1].target);
  const mappings = Object.entries(plan.mapping).sort((a, b) => b[0].length - a[0].length);
  assert.deepEqual(relocate(projects[0].storageLocation, mappings),
    { target: '/data/projects/.task-storage/PROJ-011', unresolved: false });
  assert.deepEqual(relocate(projects[1].storageLocation, mappings),
    { target: '/data/projects/.task-storage/PROJ-014', unresolved: false });
  assert.deepEqual(plan.issues, []);
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

test('finds known token shapes and leaves ordinary text alone', () => {
  const positives = [
    ['github-token', `token ${fakeToken.github()}`],
    ['github-token', fakeToken.githubPat()],
    ['anthropic-key', `ANTHROPIC_API_KEY=${fakeToken.anthropic()}`],
    ['openai-key', `"key":"${fakeToken.openai()}"`],
    ['aws-access-key', `aws_access_key_id = ${fakeToken.aws()}`],
    ['google-api-key', fakeToken.google()],
    ['slack-token', fakeToken.slack()],
    ['npm-token', `//registry.npmjs.org/:_authToken=${fakeToken.npm()}`],
    ['private-key', `${fakeToken.privateKey()}\nAAAA`],
    ['url-credentials', ['https://operator', 'fake-value@github.com/example/demo.git'].join(':')],
  ];
  for (const [name, text] of positives) assert.equal(findTokenPattern(text), name, text);
  for (const text of ['git clone ssh://git@github.com/example/demo.git', 'git@github.com:example/demo.git',
    'sk-short', 'ghp_short', 'risk-assessment-for-the-quarterly-report-and-more', '{"Users":[]}']) {
    assert.equal(findTokenPattern(text), null, text);
  }
});

test('token screen excludes archive files with a token, keeps the access state, and reuses unchanged results', () => {
  const entry = (file, size, mtimeMs, extra = {}) => ({ path: file, size, mtimeMs, area: 'runtime', state: 'untracked', transfer: 'archive', ...extra });
  const scanned = [];
  const scan = file => { scanned.push(file); return file.includes('leak') ? 'github-token' : null; };
  const entries = [
    entry('.runtime/leak.log', 10, 1000),
    entry('.runtime/clean.log', 10, 1000),
    entry('.security/access-state.json', 10, 1000, { area: 'access-state' }),
    entry('projects/demo/tasks/AGT-1/task.md', 10, 1000, { transfer: 'git', state: 'tracked' }),
  ];
  const first = screenTokens(entries, { scan });
  assert.deepEqual(scanned, ['.runtime/leak.log', '.runtime/clean.log']);
  assert.deepEqual(first.map(item => [item.path, item.transfer, item.reason ?? null, item.token ?? null]), [
    ['.runtime/leak.log', 'exclude', 'token-pattern', 'github-token'],
    ['.runtime/clean.log', 'archive', null, null],
    ['.security/access-state.json', 'archive', null, null],
    ['projects/demo/tasks/AGT-1/task.md', 'git', null, null],
  ]);

  scanned.length = 0;
  const second = screenTokens([entry('.runtime/leak.log', 10, 1000), entry('.runtime/clean.log', 12, 2000)], { scan, previous: first });
  assert.deepEqual(scanned, ['.runtime/clean.log']);
  assert.equal(second[0].reason, 'token-pattern');
  assert.equal(second[1].transfer, 'archive');
});

test('file token scan finds a token across a chunk border and skips symbolic links', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-studio-scan-'));
  try {
    const file = path.join(root, 'big.log');
    await writeFile(file, 'a'.repeat((1 << 20) - 8) + ' ' + fakeToken.github() + '\n');
    assert.equal(fileTokenPattern(file), 'github-token');
    const clean = path.join(root, 'clean.log');
    await writeFile(clean, 'b'.repeat((1 << 20) + 100));
    assert.equal(fileTokenPattern(clean), null);
    await symlink(file, path.join(root, 'link.log'));
    assert.equal(fileTokenPattern(path.join(root, 'link.log')), null);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('strips credentials from repository URLs before they are logged or planned', () => {
  const secret = 'fake-' + 'secret';
  const cases = [
    [`https://operator:${secret}@github.com/example/demo.git`, 'https://github.com/example/demo.git'],
    [`https://${secret}@github.com/example/demo.git`, 'https://github.com/example/demo.git'],
    [`ssh://git:${secret}@example.invalid:2222/demo.git`, 'ssh://git@example.invalid:2222/demo.git'],
    ['ssh://git@example.invalid/demo.git', 'ssh://git@example.invalid/demo.git'],
    ['git@github.com:example/demo.git', 'git@github.com:example/demo.git'],
    ['  https://github.com/example/demo.git\n', 'https://github.com/example/demo.git'],
  ];
  for (const [input, expected] of cases) assert.equal(stripUrlCredentials(input), expected, input);

  const projects = readRegistryFromFixture().slice(0, 2);
  projects[0].urls = [{ id: 'repo', label: 'Repository', url: `https://operator:${secret}@github.com/example/demo.git` }];
  const plan = generateMapping({ workspace: fixture.workspace, projects, remotes: { 'PROJ-002': 'https://github.com/example/demo.git' } });
  assert.deepEqual(plan.issues, []);
  assert.equal(plan.checkouts[0].repositoryUrl, 'https://github.com/example/demo.git');
  assert.equal(plan.checkouts[0].strippedCredentials, true);

  const output = [];
  assert.equal(runCli(['strip-url'], { write: text => output.push(text), stdin: () => `https://${secret}@example.invalid/w.git\n` }), 0);
  assert.deepEqual(output, ['https://example.invalid/w.git\n']);
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

test('delta selection removes files sent earlier that are excluded now, except modified tracked files', () => {
  const entry = (file, extra = {}) => ({ path: file, size: 10, mtimeMs: 1000, transfer: 'archive', state: 'untracked', ...extra });
  const first = selectDelta({ entries: [
    entry('.runtime/cli-output/AGT-1.log'),
    entry('notes/scratch.md'),
    entry('projects/demo/status.md', { state: 'tracked-modified' }),
  ] });
  assert.deepEqual(first.copy, ['.runtime/cli-output/AGT-1.log', 'notes/scratch.md', 'projects/demo/status.md']);

  // A token appeared in two files after the first pass. The untracked one must
  // leave staging; the Git sync already restored the committed tracked file.
  const tokenHit = { size: 20, mtimeMs: 2000, transfer: 'exclude', reason: 'token-pattern', token: 'github-token' };
  const second = selectDelta({ entries: [
    entry('.runtime/cli-output/AGT-1.log', tokenHit),
    entry('notes/scratch.md'),
    entry('projects/demo/status.md', { ...tokenHit, state: 'tracked-modified' }),
  ], previous: first.manifest });
  assert.deepEqual(second.copy, []);
  assert.deepEqual(second.remove, ['.runtime/cli-output/AGT-1.log']);
  assert.deepEqual(Object.keys(second.manifest.files), ['notes/scratch.md']);
});

test('token hits in metadata, orchestration state, and task folders block the inventory', () => {
  const workspace = fixture.workspace;
  const projects = readRegistryFromFixture();
  const roots = taskRoots(workspace, projects);
  assert.deepEqual(roots, ['projects/demo/tasks', 'projects/docs/tasks', 'projects/demo-two/tasks', 'projects/task-only/tasks']);
  const hit = (file, area, extra = {}) => ({ path: file, area, state: 'untracked', transfer: 'exclude', reason: 'token-pattern', token: 'github-token', ...extra });
  const entries = [
    hit('.metadata/attempt-authority.json', 'attempt-authority'),
    hit('.metadata/runs/AGT-1.json', 'metadata'),
    hit('.orchestration-ai-asks/ask-1.json', 'orchestration-state'),
    hit('.orchestrator-decisions/decision.json', 'other'),
    hit('project-settings.json', 'other'),
    hit('projects/demo/tasks/3-done/AGT-1/status.md', 'other', { state: 'tracked-modified' }),
    hit('projects/elsewhere/AGT-9/results/report.html', 'results'),
    hit('.runtime/cli-output/AGT-1.log', 'runtime'),
    hit('logs/api.log', 'logs'),
    { path: '.metadata/other.json', area: 'metadata', state: 'untracked', transfer: 'exclude', reason: 'credential' },
  ];
  assert.deepEqual(tokenStateIssues(entries, roots).map(issue => [issue.code, issue.path, issue.token]), [
    ['token-in-state', '.metadata/attempt-authority.json', 'github-token'],
    ['token-in-state', '.metadata/runs/AGT-1.json', 'github-token'],
    ['token-in-state', '.orchestration-ai-asks/ask-1.json', 'github-token'],
    ['token-in-state', '.orchestrator-decisions/decision.json', 'github-token'],
    ['token-in-state', 'project-settings.json', 'github-token'],
    ['token-in-state', 'projects/demo/tasks/3-done/AGT-1/status.md', 'github-token'],
    ['token-in-state', 'projects/elsewhere/AGT-9/results/report.html', 'github-token'],
  ]);
});

test('inventory exits 3 when a task folder file contains a token', async () => {
  const { root, workspace } = await materialize({
    'projects/demo/tasks/3-done/AGT-1/notes.md': `pushed with ${fakeToken.github()}\n`,
    '.runtime/cli-output/AGT-1.log': `pushed with ${fakeToken.github()}\n`,
  });
  try {
    const output = [];
    await writeFile(path.join(root, 'remotes.tsv'), 'PROJ-003\thttps://github.com/example/demo-two.git\nPROJ-005\thttps://github.com/example/legacy.git\n');
    await writeFile(path.join(root, 'extra.json'), JSON.stringify({ 'G:\\Elsewhere\\legacy-root': '/data/projects/legacy/root' }));
    const status = runCli(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace, '--home', fixture.home,
      '--remotes', path.join(root, 'remotes.tsv'), '--extra-mapping', path.join(root, 'extra.json'), '--out-dir', root],
    { now: fixedClock, write: text => output.push(text) });
    assert.equal(status, 3);
    const inventory = JSON.parse(await readFile(path.join(root, 'inventory.json'), 'utf8'));
    assert.deepEqual(inventory.issues.map(issue => [issue.code, issue.path]), [['token-in-state', 'projects/demo/tasks/3-done/AGT-1/notes.md']]);
    assert.match(output.join(''), /not copied \(contains a github-token\): \.runtime\/cli-output\/AGT-1\.log/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('external task storage blocks on token hits and missing source despite extra mapping', async () => {
  const { root, workspace } = await materialize({});
  const task = path.join(root, 'external', '.orchestrator', 'jobs', 'AGT-1', 'task.md');
  try {
    await writeFile(task, `credential ${fakeToken.github()}\n`);
    await writeFile(path.join(root, 'extra.json'), JSON.stringify({ 'F:\\Legacy': '/data/projects/wrong' }));
    const args = ['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace, '--home', fixture.home,
      '--extra-mapping', path.join(root, 'extra.json'), '--out-dir', root];
    assert.equal(runCli(args, { now: fixedClock, write: () => {} }), 3);
    let inventory = JSON.parse(await readFile(path.join(root, 'inventory.json'), 'utf8'));
    assert.equal(inventory.issues.some(issue => issue.code === 'token-in-state' && issue.project === 'PROJ-005'), true);
    assert.equal(inventory.storageRoots[0].entries[0].transfer, 'exclude');
    await rm(path.join(root, 'external'), { recursive: true });
    assert.equal(runCli(args, { now: fixedClock, write: () => {} }), 3);
    inventory = JSON.parse(await readFile(path.join(root, 'inventory.json'), 'utf8'));
    assert.equal(inventory.issues.some(issue => issue.code === 'missing-storage-root' && issue.projects[0] === 'PROJ-005'), true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
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
    assert.match(output.join(''), /not copied \(credential\): runner\.env/);
    assert.match(output.join(''), /not copied \(contains a github-token\): \.runtime\/cli-output\/AGT-9\.log/);
    const checkouts = await readFile(path.join(state, 'checkouts.tsv'), 'utf8');
    assert.equal(checkouts.split('\n').filter(Boolean).length, 3);
    const storage = JSON.parse(await readFile(path.join(state, 'inventory.json'), 'utf8')).storageRoots[0];
    assert.equal(storage.target, '/data/projects/.task-storage/PROJ-005');
    assert.deepEqual(storage.entries.map(entry => entry.path), ['AGT-1/task.md']);
    const storageDelta = ['delta', '--inventory', path.join(state, 'inventory.json'), '--storage-id', 'PROJ-005',
      '--copy', path.join(state, 'storage-copy.z'), '--remove', path.join(state, 'storage-remove.z'),
      '--manifest', path.join(state, 'storage-manifest.json')];
    assert.equal(run(storageDelta), 0);
    assert.deepEqual((await readFile(path.join(state, 'storage-copy.z'), 'utf8')).split('\0').filter(Boolean), ['AGT-1/task.md']);

    const deltaArgs = ['delta', '--inventory', path.join(state, 'inventory.json'), '--copy', path.join(state, 'copy.z'),
      '--remove', path.join(state, 'remove.z'), '--manifest', path.join(state, 'manifest.json')];
    assert.equal(run(deltaArgs), 0);
    const firstCopy = (await readFile(path.join(state, 'copy.z'), 'utf8')).split('\0').filter(Boolean);
    assert.equal(firstCopy.includes('.security/access-state.json'), true);
    assert.equal(firstCopy.includes('.git-credentials'), false);
    assert.equal(firstCopy.includes('runner.env'), false);
    assert.equal(firstCopy.includes('.runtime/cli-output/AGT-9.log'), false);
    assert.equal(firstCopy.includes('.runtime/cli-output/AGT-1.log'), true);
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
    await mkdir(path.join(root, 'external', '.orchestrator', 'jobs', 'AGT-2'));
    await writeFile(path.join(root, 'external', '.orchestrator', 'jobs', 'AGT-2', 'task.md'), '# second task\n');
    assert.equal(run(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace,
      '--tracked', path.join(state, 'tracked.z'), '--dirty', path.join(state, 'dirty.z'),
      '--remotes', path.join(state, 'remotes.tsv'), '--extra-mapping', path.join(state, 'extra.json'),
      '--home', fixture.home, '--out-dir', state]), 0);
    assert.equal(run([...storageDelta, '--previous', path.join(state, 'storage-manifest.json'),
      '--manifest', path.join(state, 'storage-manifest.next.json')]), 0);
    assert.deepEqual((await readFile(path.join(state, 'storage-copy.z'), 'utf8')).split('\0').filter(Boolean), ['AGT-2/task.md']);
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
