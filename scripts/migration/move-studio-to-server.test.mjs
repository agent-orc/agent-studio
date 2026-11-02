// Run: node --test scripts/migration/move-studio-to-server.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rm, stat, symlink, utimes, writeFile } from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  acceptedTokenHits, anonymousProbeUrl, buildInventory, carriedVisibilities, carryVisibility, classifyAnonymousProbe,
  classifyPath, classifyWorkspace, credentialChecklist, deriveRepositoryFromStorage, fileTokenPattern, findTokenPattern,
  generateMapping, integrationBranches, inventoryExitCode, projectVisibilities, readProjectRegistry, runCli,
  scanFileContent, scanWorkspace, screenTokens, selectDelta, setRepositoryPaths, skippedFolders, stripUrlCredentials,
  taskRoots, tokenHitLines, tokenStateIssues,
} from './move-studio-to-server.mjs';
import { inventory as relocateInventory, relocate } from './relocate-workspace-paths.mjs';

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

test('refuses a workspace without a project registry instead of planning an empty migration', async () => {
  const { root, workspace } = await materialize({});
  try {
    await rm(path.join(workspace, '.metadata', 'projects.json'));
    assert.throws(() => readProjectRegistry(workspace), /Project registry not found: .*projects\.json/);
    const out = path.join(root, 'out');
    await mkdir(out);
    const output = [];
    assert.throws(() => runCli(['projects', '--workspace', workspace], { write: text => output.push(text) }), /not found/);
    assert.throws(() => runCli(['inventory', '--workspace', workspace, '--out-dir', out], { now: fixedClock, write: text => output.push(text) }),
      /not found/);
    assert.deepEqual(output, []);
    assert.equal(existsSync(path.join(out, 'inventory.json')), false);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('refuses an invalid project registry that has no backup', async () => {
  const { root, workspace } = await materialize({});
  try {
    await writeFile(path.join(workspace, '.metadata', 'projects.json'), '{ truncated');
    assert.throws(() => readProjectRegistry(workspace), /Project registry is invalid and has no backup/);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('accepts a registry that lists no projects', async () => {
  const { root, workspace } = await materialize({});
  try {
    await writeFile(path.join(workspace, '.metadata', 'projects.json'), JSON.stringify({ Projects: [] }));
    assert.deepEqual(readProjectRegistry(workspace), []);
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

test('derives a repository from in-repo task storage the way ProjectRepoResolver does', () => {
  assert.equal(deriveRepositoryFromStorage('C:\\Code\\Repo\\.orchestrator\\jobs'), 'C:\\Code\\Repo');
  assert.equal(deriveRepositoryFromStorage('C:\\Code\\Repo\\.Orchestrator\\Jobs\\'), 'C:\\Code\\Repo');
  assert.equal(deriveRepositoryFromStorage('/home/op/repo/.orchestrator/jobs'), '/home/op/repo');
  for (const none of ['C:\\.orchestrator\\jobs', '/.orchestrator/jobs', 'D:\\Tasks\\jobs', 'C:\\Repo\\.orchestrator', null]) {
    assert.equal(deriveRepositoryFromStorage(none), null, String(none));
  }
});

test('a checkout derived from task storage is cloned and gets its RepositoryPath written', () => {
  const projects = [
    { id: 'PROJ-020', displayName: 'Derived', storageLocation: 'C:\\Code\\Derived\\.orchestrator\\jobs', repositoryPath: null, rootPath: null, urls: [] },
    { id: 'PROJ-021', displayName: 'Plain folder', storageLocation: 'C:\\Code\\NoGit\\.orchestrator\\jobs', repositoryPath: null, rootPath: null, urls: [] },
  ];
  const plan = generateMapping({ workspace: 'C:\\Studio\\workspace', projects,
    remotes: { 'PROJ-020': 'git@example.invalid:derived.git' },
    isRepository: folder => folder === 'C:\\Code\\Derived' });
  assert.deepEqual(plan.checkouts.map(item => [item.name, item.source, item.repositoryUrl, item.derivedProjects]),
    [['derived', 'C:\\Code\\Derived', 'git@example.invalid:derived.git', ['PROJ-020']]]);
  assert.deepEqual(plan.repositoryPaths, { 'PROJ-020': '/data/projects/derived' });
  // Task storage still wins over the checkout prefix it lies in.
  const mappings = Object.entries(plan.mapping).sort((a, b) => b[0].length - a[0].length);
  assert.equal(relocate(projects[0].storageLocation, mappings).target, '/data/projects/.task-storage/PROJ-020');
  assert.deepEqual(plan.issues, []);

  const registry = { Projects: [
    { Id: 'PROJ-020', StorageLocation: '/data/projects/.task-storage/PROJ-020', RepositoryPath: null },
    { Id: 'PROJ-022', RepositoryPath: '/data/projects/kept' },
  ] };
  assert.deepEqual(setRepositoryPaths(registry, { ...plan.repositoryPaths, 'PROJ-022': '/data/projects/other' }), ['PROJ-020']);
  assert.equal(registry.Projects[0].RepositoryPath, '/data/projects/derived');
  assert.equal(registry.Projects[1].RepositoryPath, '/data/projects/kept');
});

test('a checkout carries its configured integration branch unless its projects disagree', () => {
  const projects = readRegistryFromFixture();
  const settings = { demo: { IntegrationBranch: 'develop' }, 'PROJ-003': { integrationBranch: ' release ' }, 'Demo docs': { IntegrationBranch: '' } };
  const branches = integrationBranches(settings, projects);
  assert.deepEqual(branches, { 'PROJ-001': 'develop', 'PROJ-003': 'release' });
  let plan = generateMapping({ workspace: fixture.workspace, projects, remotes: fixture.remotes, branches });
  assert.deepEqual(plan.checkouts.map(item => [item.name, item.integrationBranch]),
    [['demo', 'develop'], ['demo-2', 'release'], ['legacy', null]]);
  plan = generateMapping({ workspace: fixture.workspace, projects, remotes: fixture.remotes,
    branches: { ...branches, 'PROJ-002': 'main' } });
  assert.equal(plan.checkouts[0].integrationBranch, null);
});

test('only token hits make the inventory acceptable, any other issue keeps it blocked', () => {
  const token = { code: 'token-in-state', path: 'projects/demo/tasks/AGT-1/notes.md', token: 'github-token' };
  const storage = { code: 'token-in-state', project: 'PROJ-005', path: 'F:\\Legacy\\.orchestrator\\jobs/AGT-1/task.md', token: 'private-key' };
  assert.equal(inventoryExitCode([]), 0);
  assert.equal(inventoryExitCode([token, storage]), 4);
  assert.equal(inventoryExitCode([token, { code: 'missing-repository-url', projects: ['PROJ-003'] }]), 3);
  assert.deepEqual(tokenHitLines([token, storage, { code: 'unmapped-path' }]),
    ['github-token\tprojects/demo/tasks/AGT-1/notes.md', 'private-key\tF:\\Legacy\\.orchestrator\\jobs/AGT-1/task.md']);
  // Each listed line is exactly what the screen accepts on the next pass.
  const entry = { path: 'AGT-1/task.md', size: 1, mtimeMs: 1, transfer: 'archive', area: 'other' };
  const screened = screenTokens([entry], { scan: () => ({ hash: 'h', token: 'private-key' }),
    accepted: new Set(tokenHitLines([storage])), prefix: 'F:\\Legacy\\.orchestrator\\jobs/' });
  assert.equal(screened[0].transfer, 'archive');
});

test('the server-side repository-paths command links derived projects in the registry and its backup', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-repo-paths-'));
  const metadata = path.join(root, 'workspace', '.metadata');
  await mkdir(metadata, { recursive: true });
  const registry = { Projects: [{ Id: 'PROJ-020', RepositoryPath: null }, { Id: 'PROJ-001', RepositoryPath: '/data/projects/demo' }] };
  await writeFile(path.join(metadata, 'projects.json'), '\uFEFF' + JSON.stringify(registry));
  await writeFile(path.join(metadata, 'projects.json.bak'), JSON.stringify(registry));
  await writeFile(path.join(root, 'paths.json'), JSON.stringify({ 'PROJ-020': '/data/projects/derived' }));
  const output = [];
  try {
    assert.equal(runCli(['repository-paths', '--workspace', path.join(root, 'workspace'), '--paths', path.join(root, 'paths.json')],
      { write: text => output.push(text) }), 0);
    assert.equal(output.join(''), 'RepositoryPath written for PROJ-020\n');
    const primary = await readFile(path.join(metadata, 'projects.json'), 'utf8');
    assert.ok(primary.startsWith('\uFEFF'));
    assert.equal(JSON.parse(primary.slice(1)).Projects[0].RepositoryPath, '/data/projects/derived');
    assert.equal(JSON.parse(await readFile(path.join(metadata, 'projects.json.bak'), 'utf8')).Projects[0].RepositoryPath, '/data/projects/derived');
    assert.equal(readProjectRegistry(path.join(root, 'workspace'))[1].repositoryPath, '/data/projects/demo');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
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

test('token screen reads every archive file even when size and time match', () => {
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
  assert.deepEqual(scanned, ['.runtime/leak.log', '.runtime/clean.log', '.security/access-state.json']);
  assert.deepEqual(first.map(item => [item.path, item.transfer, item.reason ?? null, item.token ?? null]), [
    ['.runtime/leak.log', 'exclude', 'token-pattern', 'github-token'],
    ['.runtime/clean.log', 'archive', null, null],
    ['.security/access-state.json', 'archive', null, null],
    ['projects/demo/tasks/AGT-1/task.md', 'git', null, null],
  ]);

  scanned.length = 0;
  const second = screenTokens([entry('.runtime/leak.log', 10, 1000), entry('.runtime/clean.log', 12, 2000)], { scan });
  assert.deepEqual(scanned, ['.runtime/leak.log', '.runtime/clean.log']);
  assert.equal(second[0].reason, 'token-pattern');
  assert.equal(second[1].transfer, 'archive');
  const tracked = entry('notes/modified.md', 40, 1000, { state: 'tracked-modified' });
  const clean = screenTokens([tracked], { scan: () => ({ hash: 'before', token: null }) });
  assert.equal(clean[0].transfer, 'archive');
  const changed = screenTokens([tracked], { scan: () => ({ hash: 'after', token: 'github-token' }) });
  assert.equal(changed[0].transfer, 'exclude');
  assert.equal(changed[0].token, 'github-token');
});

test('same-metadata token in a modified tracked file is screened again', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-studio-token-'));
  try {
    const file = path.join(root, 'status.md');
    const token = fakeToken.github();
    await writeFile(file, 'a'.repeat(token.length));
    await utimes(file, fixedClock(), fixedClock());
    const time = await stat(file);
    const entry = { path: 'projects/demo/tasks/AGT-1/status.md', size: token.length, mtimeMs: Math.trunc(time.mtimeMs),
      area: 'other', state: 'tracked-modified', transfer: 'archive' };
    const first = screenTokens([entry], { scan: () => scanFileContent(file) });
    await writeFile(file, token);
    await utimes(file, time.atime, time.mtime);
    const after = await stat(file);
    assert.equal(after.size, time.size);
    assert.equal(Math.trunc(after.mtimeMs), Math.trunc(time.mtimeMs));
    const second = screenTokens([entry], { scan: () => scanFileContent(file) });
    assert.notEqual(second[0].hash, first[0].hash);
    assert.equal(second[0].transfer, 'exclude');
    assert.deepEqual(tokenStateIssues(second, ['projects/demo/tasks']).map(issue => issue.code), ['token-in-state']);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
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
  const entry = (file, size, mtimeMs, extra = {}) => ({ path: file, size, mtimeMs, hash: `${size}-${mtimeMs}`, transfer: 'archive', state: 'untracked', ...extra });
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

test('delta selection copies a task edit with unchanged size and modification time', () => {
  const task = { path: 'AGT-1/task.md', size: 12, mtimeMs: 1000, hash: 'before', transfer: 'archive', state: 'untracked' };
  const first = selectDelta({ entries: [task] });
  assert.deepEqual(selectDelta({ entries: [{ ...task, hash: 'after' }], previous: first.manifest }).copy, ['AGT-1/task.md']);
  assert.deepEqual(selectDelta({ entries: [task], previous: first.manifest }).copy, []);
});

test('delta selection removes files sent earlier that are excluded now, except modified tracked files', () => {
  const entry = (file, extra = {}) => ({ path: file, size: 10, mtimeMs: 1000, hash: 'unchanged', transfer: 'archive', state: 'untracked', ...extra });
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

test('inventory exits 4 and lists the acceptable hit when only a task folder token blocks', async () => {
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
    assert.equal(status, 4);
    const inventory = JSON.parse(await readFile(path.join(root, 'inventory.json'), 'utf8'));
    assert.deepEqual(inventory.issues.map(issue => [issue.code, issue.path]), [['token-in-state', 'projects/demo/tasks/3-done/AGT-1/notes.md']]);
    assert.match(output.join(''), /not copied \(contains a github-token\): \.runtime\/cli-output\/AGT-1\.log/);
    assert.match(output.join(''), /Only token hits block/);
    const hits = path.join(root, 'token-hits.tsv');
    assert.equal(await readFile(hits, 'utf8'), 'github-token\tprojects/demo/tasks/3-done/AGT-1/notes.md\n');

    // Accepting the listed lines lets the same pass continue; the file then travels.
    process.env.MOVE_ACCEPTED_TOKEN_HITS = hits;
    try {
      const again = runCli(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace, '--home', fixture.home,
        '--remotes', path.join(root, 'remotes.tsv'), '--extra-mapping', path.join(root, 'extra.json'), '--out-dir', root],
      { now: fixedClock, write: () => {} });
      assert.equal(again, 0);
    } finally {
      delete process.env.MOVE_ACCEPTED_TOKEN_HITS;
    }
    const accepted = JSON.parse(await readFile(path.join(root, 'inventory.json'), 'utf8'));
    assert.equal(accepted.entries.find(entry => entry.path === 'projects/demo/tasks/3-done/AGT-1/notes.md').transfer, 'archive');
    assert.equal(await readFile(hits, 'utf8'), '');
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
    const task = path.join(root, 'external', '.orchestrator', 'jobs', 'AGT-1', 'task.md');
    await utimes(task, fixedClock(), fixedClock());
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
    const bus = path.join(workspace, 'logs', 'bus', '2026-10-18.jsonl');
    await writeFile(bus, 'changed bus contents\n');
    await utimes(bus, later, later);
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
    const taskTime = await stat(task);
    await writeFile(task, '# revised  task\n');
    await utimes(task, taskTime.atime, taskTime.mtime);
    const taskAfter = await stat(task);
    assert.equal(taskAfter.size, taskTime.size);
    assert.equal(Math.trunc(taskAfter.mtimeMs), Math.trunc(taskTime.mtimeMs));
    assert.equal(run(['inventory', '--workspace', workspace, '--source-workspace', fixture.workspace,
      '--tracked', path.join(state, 'tracked.z'), '--dirty', path.join(state, 'dirty.z'),
      '--remotes', path.join(state, 'remotes.tsv'), '--extra-mapping', path.join(state, 'extra.json'),
      '--home', fixture.home, '--out-dir', state]), 0);
    assert.equal(run([...storageDelta, '--previous', path.join(state, 'storage-manifest.next.json'),
      '--manifest', path.join(state, 'storage-manifest.changed.json')]), 0);
    assert.deepEqual((await readFile(path.join(state, 'storage-copy.z'), 'utf8')).split('\0').filter(Boolean), ['AGT-1/task.md']);
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

test('an operator-reviewed token hit travels, and a new shape in that file still blocks', async () => {
  const dir = await mkdtemp(path.join(os.tmpdir(), 'move-accepted-'));
  const file = path.join(dir, 'accepted.tsv');
  await writeFile(file, 'github-token\tprojects/demo/results/test.log\n');
  const entry = { path: 'projects/demo/results/test.log', size: 1, mtimeMs: 1, transfer: 'archive', area: 'results' };
  const accepted = acceptedTokenHits(file);

  const fixture = screenTokens([entry], { scan: () => ({ hash: 'h', token: 'github-token' }), accepted });
  assert.equal(fixture[0].transfer, 'archive');
  const other = screenTokens([entry], { scan: () => ({ hash: 'h', token: 'private-key' }), accepted });
  assert.equal(other[0].reason, 'token-pattern');
  assert.equal(acceptedTokenHits(undefined).size, 0);
  assert.equal(acceptedTokenHits(path.join(dir, 'not-written-yet.tsv')).size, 0);
  await rm(dir, { recursive: true, force: true });
});

test('skipped folders stay out of the workspace scan', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-skip-'));
  await mkdir(path.join(root, '.claude/worktrees/old'), { recursive: true });
  await mkdir(path.join(root, 'projects/demo'), { recursive: true });
  await writeFile(path.join(root, '.claude/worktrees/old/copy.md'), 'x');
  await writeFile(path.join(root, 'projects/demo/keep.md'), 'x');

  const skip = skippedFolders('.claude/worktrees: :');
  assert.deepEqual(skip, ['.claude/worktrees']);
  assert.deepEqual(scanWorkspace(root, { skip }).map(item => item.path), ['projects/demo/keep.md']);
  await rm(root, { recursive: true, force: true });
});

test('a JSON file with a byte order mark is relocated and keeps its mark', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-bom-'));
  const workspace = path.join(root, 'workspace');
  await mkdir(path.join(workspace, '.metadata'), { recursive: true });
  await writeFile(path.join(workspace, '.metadata', 'session.json'), '\uFEFF{"root":"C:\\\\Projects\\\\demo"}');
  const mapping = path.join(root, 'map.json');
  await writeFile(mapping, JSON.stringify({ 'C:\\Projects\\demo': '/data/projects/demo' }));

  const report = await relocateInventory({ workspace, mapping, apply: true });

  assert.equal(report.unresolved?.length ?? 0, 0);
  const written = await readFile(path.join(workspace, '.metadata', 'session.json'), 'utf8');
  assert.ok(written.startsWith('\uFEFF'));
  assert.ok(written.includes('/data/projects/demo'));
  await rm(root, { recursive: true, force: true });
});

test('a store set aside after a crash stays behind', () => {
  assert.equal(classifyPath('.metadata/attempt-authority.corrupt-20261011-1647.json').transfer, 'exclude');
  assert.equal(classifyPath('.metadata/attempt-authority.json').transfer, 'archive');
});

test('a Windows path inside stored message text is history and does not stop the apply', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-text-'));
  const workspace = path.join(root, 'workspace');
  await mkdir(path.join(workspace, '.metadata'), { recursive: true });
  const summary = 'finished failed=0 report=C:\\Users\\someone\\report.json';
  await writeFile(path.join(workspace, '.metadata', 'attempts.json'), JSON.stringify({ root: 'C:\\Projects\\demo', summary }));
  const mapping = path.join(root, 'map.json');
  await writeFile(mapping, JSON.stringify({ 'C:\\Projects\\demo': '/data/projects/demo' }));

  const report = await relocateInventory({ workspace, mapping, apply: true });

  assert.equal(report.unresolved, 0);
  const written = JSON.parse(await readFile(path.join(workspace, '.metadata', 'attempts.json'), 'utf8'));
  assert.equal(written.root, '/data/projects/demo');
  assert.equal(written.summary, summary);
  await rm(root, { recursive: true, force: true });
});

test('the anonymous probe URL never carries a credential and reaches GitHub ssh remotes over https', () => {
  const password = 'fake-' + 'secret';
  assert.equal(anonymousProbeUrl('git@github.com:Owner/Site.git'), 'https://github.com/Owner/Site.git');
  assert.equal(anonymousProbeUrl('ssh://git@github.com/owner/site'), 'https://github.com/owner/site.git');
  assert.equal(anonymousProbeUrl(`https://operator:${password}@github.com/owner/site.git`), 'https://github.com/owner/site.git');
  assert.equal(anonymousProbeUrl('https://gitlab.example/owner/site.git'), 'https://gitlab.example/owner/site.git');
  assert.equal(anonymousProbeUrl('git@gitlab.example:owner/site.git'), null);
  assert.equal(anonymousProbeUrl(''), null);
});

test('an anonymous probe that asks for credentials marks the repository private', () => {
  assert.equal(classifyAnonymousProbe({ exitCode: 0, stderr: '' }), 'public');
  assert.equal(classifyAnonymousProbe({ exitCode: 128,
    stderr: "fatal: could not read Username for 'https://github.com': terminal prompts disabled" }), 'private');
  assert.equal(classifyAnonymousProbe({ exitCode: 128, stderr: "remote: Repository not found." }), 'private');
  assert.equal(classifyAnonymousProbe({ exitCode: 128, stderr: 'fatal: unable to access: Could not resolve host' }), null);
  assert.equal(classifyAnonymousProbe({ exitCode: 124, stderr: '' }), null);
});

test('a project setting outranks the probe and an unprobed project stays unknown', () => {
  const projects = [
    { id: 'P1', displayName: 'Set' }, { id: 'P2', displayName: 'Probed' }, { id: 'P3', displayName: 'Silent' },
  ];
  const settings = { set: { repositoryVisibility: 'Private' }, Probed: { AutoCommit: true } };
  const probes = {
    P1: { exitCode: 0, stderr: '' },
    P2: { exitCode: 128, stderr: 'fatal: Authentication failed' },
    P3: { exitCode: 128, stderr: 'fatal: Could not resolve host' },
  };

  assert.deepEqual(projectVisibilities({ projects, settings, probes }), [
    { id: 'P1', displayName: 'Set', visibility: 'private', source: 'setting' },
    { id: 'P2', displayName: 'Probed', visibility: 'private', source: 'probe' },
    { id: 'P3', displayName: 'Silent', visibility: null, source: 'unknown' },
  ]);
});

test('the inventory reports each visibility and carries only probe results', () => {
  const inventory = buildInventory({
    workspace: '/w', files: [], tracked: [], dirty: [], remotes: {}, extra: {}, home: '/h', exists: () => false,
    credentialHelper: '', now: fixedClock,
    projects: [
      { id: 'P1', displayName: 'Set', urls: [] }, { id: 'P2', displayName: 'Probed', urls: [] },
      { id: 'P3', displayName: 'Silent', urls: [] },
    ],
    settings: { Set: { RepositoryVisibility: 'public' } },
    probes: { P1: { exitCode: 128, stderr: 'Repository not found' }, P2: { exitCode: 128, stderr: 'Repository not found' } },
  });

  assert.deepEqual(inventory.projects.map(project => [project.repositoryVisibility, project.visibilitySource]),
    [['public', 'setting'], ['private', 'probe'], [null, 'unknown']]);
  assert.deepEqual(carriedVisibilities(inventory), { Probed: 'private' });
});

test('carrying a visibility never overwrites one the server copy already has', () => {
  const { settings, changed } = carryVisibility(
    { 'probed site': { AutoCommit: true }, Kept: { RepositoryVisibility: 'public' } },
    { 'Probed site': 'private', Kept: 'private', New: 'public', Bad: 'secret' });

  assert.deepEqual(changed, ['Probed site', 'New']);
  assert.deepEqual(settings, {
    'probed site': { AutoCommit: true, RepositoryVisibility: 'private' },
    Kept: { RepositoryVisibility: 'public' },
    New: { RepositoryVisibility: 'public' },
  });
});

test('carry-visibility writes project-settings.json in the server copy', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'move-visibility-'));
  await writeFile(path.join(root, 'project-settings.json'), JSON.stringify({ Site: { AutoCommit: true } }));
  const carried = path.join(root, 'visibility.json');
  await writeFile(carried, JSON.stringify({ Site: 'private' }));
  let output = '';

  assert.equal(runCli(['carry-visibility', '--workspace', root, '--visibility', carried], { write: text => { output += text; } }), 0);

  assert.equal(output, 'Repository visibility carried for 1 project(s): Site\n');
  const written = JSON.parse(await readFile(path.join(root, 'project-settings.json'), 'utf8'));
  assert.deepEqual(written, { Site: { AutoCommit: true, RepositoryVisibility: 'private' } });
  await rm(root, { recursive: true, force: true });
});
