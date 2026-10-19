#!/usr/bin/env node
// Pure planning half of move-studio-to-server.sh: workspace inventory
// classification, path mapping generation, and delta selection. The shell
// script owns every process, SSH, Git, tar, and Docker side effect; this module
// only reads the workspace and writes plan files into the local state directory.
import { existsSync, lstatSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const SERVER_WORKSPACE = '/data/workspace';
export const SERVER_PROJECTS = '/data/projects';

const windowsPath = /^(?:[a-z]:[\\/]|\\\\)/i;
const normalized = value => value.replaceAll('\\', '/').replace(/\/+$/, '');
const comparable = value => normalized(value).toLowerCase();
const isUnder = (value, prefix) => {
  const source = comparable(value);
  const root = comparable(prefix);
  return source === root || source.startsWith(root + '/');
};

const skippedDirectories = new Set(['.git', 'node_modules']);
const credentialNames = new Set([
  '.git-credentials', '.credentials.json', 'credentials.json', 'oauth_creds.json', 'auth.json',
  'provider-auth.env', 'id_rsa', 'id_ecdsa', 'id_ed25519', 'id_dsa', '.netrc', '_netrc',
]);
const credentialExtensions = ['.pem', '.key', '.pfx', '.p12'];

function isCredentialName(name) {
  const lower = name.toLowerCase();
  if (credentialNames.has(lower)) return true;
  if (lower === '.env' || (lower.startsWith('.env.') && lower !== '.env.example')) return true;
  return credentialExtensions.some(extension => lower.endsWith(extension));
}

function isTransient(relative) {
  const lower = relative.toLowerCase();
  return lower.endsWith('.tmp') || /\.relocate-\d+$/.test(lower) || lower.split('/').includes('.migration-backups');
}

/** Workspace area of one relative path, independent of its Git state. */
export function areaOf(relative) {
  const value = normalized(relative);
  const segments = value.split('/');
  if (value.startsWith('.metadata/attempt-authority')) return 'attempt-authority';
  if (segments[0] === '.metadata') return 'metadata';
  if (segments[0] === '.security') return 'access-state';
  if (segments[0] === '.runtime') return 'runtime';
  if (segments[0].startsWith('.orchestration-')) return 'orchestration-state';
  if (segments[0] === 'logs' && segments[1] === 'bus') return 'bus-log';
  if (segments.slice(0, -1).includes('results')) return 'results';
  if (segments.slice(0, -1).includes('attachments')) return 'attachments';
  if (segments[0] === 'logs') return 'logs';
  return 'other';
}

/**
 * Decides how one workspace file reaches the server. Clean tracked files come
 * with the Git history; everything else travels in the streamed archive, except
 * credential-shaped and transient files, which never leave the workstation.
 */
export function classifyPath(relative, { tracked = false, dirty = false, present = true } = {}) {
  const value = normalized(relative);
  const area = areaOf(value);
  if (tracked && !present) return { area, state: 'tracked-deleted', transfer: 'remove' };
  if (tracked && !dirty) {
    return isCredentialName(path.posix.basename(value))
      ? { area, state: 'tracked', transfer: 'git', reason: 'credential-shaped file already in Git history' }
      : { area, state: 'tracked', transfer: 'git' };
  }
  const state = tracked ? 'tracked-modified' : 'untracked';
  if (isCredentialName(path.posix.basename(value))) return { area, state, transfer: 'exclude', reason: 'credential' };
  if (isTransient(value)) return { area, state, transfer: 'exclude', reason: 'transient' };
  if (area === 'access-state') return { area, state, transfer: 'archive', reason: 'Studio accounts and sessions; protect as credential material' };
  return { area, state, transfer: 'archive' };
}

/** Lists every file and symlink below the workspace except Git internals and dependency folders. */
export function scanWorkspace(root) {
  const found = [];
  const walk = relative => {
    for (const item of readdirSync(path.join(root, relative), { withFileTypes: true })) {
      if (item.isDirectory() && skippedDirectories.has(item.name)) continue;
      const child = relative ? `${relative}/${item.name}` : item.name;
      if (item.isDirectory()) { walk(child); continue; }
      if (!item.isFile() && !item.isSymbolicLink()) continue;
      const stats = lstatSync(path.join(root, child));
      found.push({ path: child, size: stats.isSymbolicLink() ? 0 : stats.size, mtimeMs: Math.trunc(stats.mtimeMs) });
    }
  };
  walk('');
  return found.sort((a, b) => a.path.localeCompare(b.path));
}

/** Joins the scanned files with Git's tracked and dirty lists and classifies each path. */
export function classifyWorkspace({ files, tracked = [], dirty = [] }) {
  const trackedSet = new Set(tracked.map(normalized));
  const dirtySet = new Set(dirty.map(normalized));
  const seen = new Set();
  const entries = files.map(file => {
    const relative = normalized(file.path);
    seen.add(relative);
    return { ...file, path: relative, ...classifyPath(relative, { tracked: trackedSet.has(relative), dirty: dirtySet.has(relative) }) };
  });
  for (const relative of [...trackedSet].sort()) {
    if (!seen.has(relative)) entries.push({ path: relative, size: 0, mtimeMs: 0, ...classifyPath(relative, { tracked: true, present: false }) });
  }
  return entries;
}

export function summarize(entries) {
  const areas = {};
  const totals = { files: 0, bytes: 0, git: 0, archive: 0, exclude: 0, remove: 0, archiveBytes: 0 };
  for (const entry of entries) {
    const area = (areas[entry.area] ??= { files: 0, bytes: 0, tracked: 0, untracked: 0 });
    area.files++;
    area.bytes += entry.size;
    if (entry.state === 'untracked') area.untracked++;
    else area.tracked++;
    totals.files++;
    totals.bytes += entry.size;
    totals[entry.transfer]++;
    if (entry.transfer === 'archive') totals.archiveBytes += entry.size;
  }
  return { totals, areas };
}

const field = (record, name) => {
  if (!record || typeof record !== 'object') return undefined;
  const key = Object.keys(record).find(candidate => candidate.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : record[key];
};

/** Reads the registry, falling back to the backup like the API does when the primary file is absent or invalid. */
export function readProjectRegistry(workspace) {
  const primary = path.join(workspace, '.metadata', 'projects.json');
  for (const candidate of [primary, primary + '.bak']) {
    if (!existsSync(candidate)) continue;
    let parsed;
    try { parsed = JSON.parse(readFileSync(candidate, 'utf8')); }
    catch (error) {
      if (candidate === primary) continue;
      throw new Error(`Project registry backup is invalid: ${error.message}`);
    }
    const projects = field(parsed, 'Projects');
    if (!Array.isArray(projects)) throw new Error(`Project registry has no Projects array: ${candidate}`);
    return projects.map(project => ({
      id: String(field(project, 'Id') ?? ''),
      displayName: String(field(project, 'DisplayName') ?? ''),
      storageLocation: field(project, 'StorageLocation') || null,
      repositoryPath: field(project, 'RepositoryPath') || null,
      rootPath: field(project, 'RootPath') || null,
      archived: field(project, 'Archived') === true,
      urls: (field(project, 'Urls') ?? []).map(url => ({
        id: String(field(url, 'Id') ?? ''), label: String(field(url, 'Label') ?? ''), url: String(field(url, 'Url') ?? ''),
      })),
    }));
  }
  return [];
}

const scpRemote = /^[^\s/@:]+@[^\s/:]+:.+$/;
const isUsableRemote = value => {
  const trimmed = (value ?? '').trim();
  if (!trimmed) return false;
  if (scpRemote.test(trimmed)) return true;
  return /^(?:https?|ssh|git):\/\/[^\s]+$/i.test(trimmed);
};

/** Same rule as RemoteProjectRepositoryResolver: the project URL whose id or label is "repo" or "repository". */
export function registryRepositoryUrl(project) {
  const entry = project.urls.find(url => url.id.toLowerCase() === 'repo'
    || ['repo', 'repository'].includes(url.label.toLowerCase()));
  return isUsableRemote(entry?.url) ? entry.url.trim() : null;
}

const slug = value => normalized(value).split('/').pop().toLowerCase().replace(/[^a-z0-9._-]+/g, '-').replace(/^[-.]+|-+$/g, '') || 'project';

/**
 * Builds the relocate-workspace-paths mapping: the workspace prefix goes to
 * /data/workspace and every checkout outside it to /data/projects/<name>.
 * Checkouts are not copied; the server clones each from its repository URL
 * (registry URL first, the workstation's origin remote second).
 */
export function generateMapping({ workspace, projects, remotes = {}, extra = {} }) {
  const mapping = {};
  const issues = [];
  const extraPrefixes = Object.keys(extra);
  const covered = value => isUnder(value, workspace) || extraPrefixes.some(prefix => isUnder(value, prefix));
  if (windowsPath.test(workspace)) mapping[workspace] = SERVER_WORKSPACE;

  const byCheckout = new Map();
  for (const project of projects) {
    const repository = project.repositoryPath ?? project.rootPath;
    if (!repository || covered(repository)) continue;
    const key = comparable(repository);
    const checkout = byCheckout.get(key) ?? { source: repository, projects: [], urls: [] };
    checkout.projects.push(project.id);
    const url = registryRepositoryUrl(project) ?? (isUsableRemote(remotes[project.id]) ? remotes[project.id].trim() : null);
    if (url) checkout.urls.push(url);
    byCheckout.set(key, checkout);
  }

  const used = new Set();
  const checkouts = [...byCheckout.values()]
    .sort((a, b) => comparable(a.source).localeCompare(comparable(b.source)))
    .map(checkout => {
      const base = slug(checkout.source);
      let name = base;
      for (let index = 2; used.has(name); index++) name = `${base}-${index}`;
      used.add(name);
      const target = `${SERVER_PROJECTS}/${name}`;
      const urls = [...new Set(checkout.urls)];
      if (urls.length === 0) issues.push({ code: 'missing-repository-url', projects: checkout.projects, path: checkout.source });
      if (urls.length > 1) issues.push({ code: 'conflicting-repository-urls', projects: checkout.projects, urls });
      if (windowsPath.test(checkout.source)) mapping[checkout.source] = target;
      return { name, source: checkout.source, target, repositoryUrl: urls[0] ?? null, projects: checkout.projects };
    });

  const mapped = value => covered(value) || checkouts.some(checkout => isUnder(value, checkout.source));
  for (const project of projects) {
    for (const [kind, value] of [['StorageLocation', project.storageLocation], ['RootPath', project.rootPath]]) {
      if (value && windowsPath.test(value) && !mapped(value)) issues.push({ code: 'unmapped-path', projects: [project.id], field: kind, path: value });
    }
  }
  for (const [from, to] of Object.entries(extra)) {
    if (!windowsPath.test(from) || typeof to !== 'string' || !to.startsWith('/')) throw new Error(`Invalid extra mapping: ${from}`);
    mapping[from] = to;
  }
  return { mapping, checkouts, issues };
}

/**
 * Replacement step for every credential the move must not copy. Each one is
 * bound to the identity and host that created it; the server gets its own.
 */
export function credentialChecklist({ home, exists, credentialHelper = '' }) {
  const at = relative => path.join(home, relative);
  const anyPresent = relatives => relatives.some(relative => exists(at(relative)));
  const items = [
    { id: 'claude', label: 'Claude Code login', paths: ['.claude/.credentials.json', '.claude.json'],
      replacement: 'Sign in once inside the server stack (docker compose exec api claude); the state persists in <data>/home.' },
    { id: 'codex', label: 'Codex login', paths: ['.codex/auth.json'],
      replacement: 'Run codex login inside the server stack; the state persists in <data>/home.' },
    { id: 'gemini', label: 'Gemini login', paths: ['.gemini/oauth_creds.json'],
      replacement: 'Run gemini inside the server stack and complete its sign-in; the state persists in <data>/home.' },
    { id: 'copilot', label: 'GitHub Copilot CLI login', paths: ['.copilot', '.config/github-copilot'],
      replacement: 'Sign in to Copilot CLI on the server with its own device login.' },
    { id: 'github-cli', label: 'GitHub CLI token', paths: ['.config/gh/hosts.yml', 'AppData/Roaming/GitHub CLI/hosts.yml'],
      replacement: 'Run gh auth login on the server with a token scoped to the server.' },
    { id: 'ssh-keys', label: 'Workstation SSH private keys', paths: ['.ssh/id_ed25519', '.ssh/id_rsa', '.ssh/id_ecdsa'],
      replacement: 'Generate a new key in <data>/home/.ssh on the server and register it as a deploy key.' },
  ].map(item => ({ id: item.id, label: item.label, present: anyPresent(item.paths), paths: item.paths.map(at), replacement: item.replacement }));
  const manager = /manager/i.test(credentialHelper);
  items.push({
    id: 'git-credential-manager', label: 'Git Credential Manager entries', present: manager,
    paths: manager ? [`credential.helper=${credentialHelper.trim()}`] : [],
    replacement: 'Never export Credential Manager entries. Give the server its own deploy key or fine-grained token in <data>/home (git credential store or ~/.ssh).',
  });
  items.push({
    id: 'runner-credentials', label: 'Remote runner bearer credentials', present: true, paths: [],
    replacement: 'Runner credentials stay on their hosts and remain valid; point each runner at https://<domain> after apply.',
  });
  return items;
}

/**
 * Picks what the next archive pass must send. A file is copied when it is new
 * or its size or modification time changed since the previous manifest.
 * Modified tracked files are always copied because the Git sync resets the
 * server working tree first. Paths that vanished from the source, and tracked
 * files deleted in the working tree, are removed on the server.
 */
export function selectDelta({ entries, previous = null }) {
  const before = previous?.files ?? {};
  const present = new Set(entries.map(entry => entry.path));
  const copy = [];
  const remove = [];
  const files = {};
  for (const entry of entries) {
    assertSafeRelative(entry.path);
    if (entry.transfer === 'remove') { remove.push(entry.path); continue; }
    if (entry.transfer !== 'archive') continue;
    files[entry.path] = { size: entry.size, mtimeMs: entry.mtimeMs };
    const last = before[entry.path];
    if (entry.state === 'tracked-modified' || !last || last.size !== entry.size || last.mtimeMs !== entry.mtimeMs) copy.push(entry.path);
  }
  for (const relative of Object.keys(before)) {
    assertSafeRelative(relative);
    if (!present.has(relative)) remove.push(relative);
  }
  return { copy: copy.sort(), remove: [...new Set(remove)].sort(), manifest: { files }, unchanged: Object.keys(files).length - copy.length };
}

export function assertSafeRelative(relative) {
  if (!relative || relative.startsWith('/') || windowsPath.test(relative) || /[\0\n\r]/.test(relative)
    || normalized(relative).split('/').some(segment => segment === '..' || segment === ''))
    throw new Error(`Unsafe workspace path: ${JSON.stringify(relative)}`);
}

/** Inventory plan for the state directory. The clock is injected so the plan is reproducible in tests. */
export function buildInventory({ workspace, files, tracked, dirty, projects, remotes, extra, home, exists, credentialHelper, now }) {
  const entries = classifyWorkspace({ files, tracked, dirty });
  const plan = generateMapping({ workspace, projects, remotes, extra });
  return {
    generatedAt: now().toISOString(),
    workspace,
    summary: summarize(entries),
    projects: projects.map(project => ({
      id: project.id, displayName: project.displayName, archived: project.archived,
      storageLocation: project.storageLocation, repositoryPath: project.repositoryPath, rootPath: project.rootPath,
    })),
    mapping: plan.mapping,
    checkouts: plan.checkouts,
    issues: plan.issues,
    credentials: credentialChecklist({ home, exists, credentialHelper }),
    entries,
  };
}

export function formatInventory(inventory) {
  const mb = bytes => (bytes / 1048576).toFixed(1) + ' MB';
  const lines = [`Workspace ${inventory.workspace}: ${inventory.summary.totals.files} files, ${mb(inventory.summary.totals.bytes)}`];
  const { totals } = inventory.summary;
  lines.push(`  via Git history: ${totals.git} files; via archive: ${totals.archive} files (${mb(totals.archiveBytes)}); excluded: ${totals.exclude}; deleted tracked: ${totals.remove}`);
  for (const [area, value] of Object.entries(inventory.summary.areas).sort()) {
    lines.push(`  ${area.padEnd(20)} ${String(value.files).padStart(7)} files ${mb(value.bytes).padStart(10)}  tracked ${value.tracked}, untracked ${value.untracked}`);
  }
  for (const entry of inventory.entries.filter(item => item.transfer === 'exclude' && item.reason === 'credential')) {
    lines.push(`  not copied (credential): ${entry.path}`);
  }
  lines.push('Registered projects:');
  for (const project of inventory.projects) {
    lines.push(`  ${project.id} ${project.displayName}${project.archived ? ' (archived)' : ''}`);
    for (const [label, value] of [['storage', project.storageLocation], ['repository', project.repositoryPath], ['root', project.rootPath]]) {
      if (value) lines.push(`    ${label}: ${value}`);
    }
  }
  lines.push('Path mapping:');
  for (const [from, to] of Object.entries(inventory.mapping)) lines.push(`  ${from} -> ${to}`);
  lines.push('Server checkouts (cloned, not copied):');
  for (const checkout of inventory.checkouts) lines.push(`  ${checkout.target} <- ${checkout.repositoryUrl ?? 'NO REPOSITORY URL'} (${checkout.projects.join(', ')})`);
  lines.push('Credentials that are not copied:');
  for (const item of inventory.credentials) lines.push(`  ${item.present ? '[found]' : '[absent]'} ${item.label}: ${item.replacement}`);
  if (inventory.issues.length) {
    lines.push('Blocking issues:');
    for (const issue of inventory.issues) lines.push(`  ${issue.code}: ${JSON.stringify(issue)}`);
  }
  return lines.join('\n');
}

const readList = file => (file && existsSync(file) ? readFileSync(file, 'utf8').split('\0').filter(Boolean) : []);
const readJson = (file, fallback) => (file && existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : fallback);

function parseArgs(args) {
  const options = {};
  for (let index = 0; index < args.length; index++) {
    if (!args[index].startsWith('--') || args[index + 1] === undefined) throw new Error(`Unknown or incomplete option: ${args[index]}`);
    options[args[index].slice(2)] = args[++index];
  }
  return options;
}

function readRemotes(file) {
  const remotes = {};
  if (!file || !existsSync(file)) return remotes;
  for (const line of readFileSync(file, 'utf8').split(/\r?\n/)) {
    const [id, url] = line.split('\t');
    if (id && url) remotes[id] = url;
  }
  return remotes;
}

export function runCli(argv, { now = () => new Date(), write = text => process.stdout.write(text) } = {}) {
  const [command, ...rest] = argv;
  const options = parseArgs(rest);
  if (command === 'projects') {
    for (const project of readProjectRegistry(options.workspace)) {
      const repository = project.repositoryPath ?? project.rootPath;
      if (repository) write(`${project.id}\t${repository}\n`);
    }
    return 0;
  }
  if (command === 'inventory') {
    const inventory = buildInventory({
      // The registry records the workspace in its Windows form; Git Bash may hand a different spelling to --workspace.
      workspace: options['source-workspace'] ?? options.workspace,
      files: scanWorkspace(options.workspace),
      tracked: readList(options.tracked),
      dirty: readList(options.dirty),
      projects: readProjectRegistry(options.workspace),
      remotes: readRemotes(options.remotes),
      extra: readJson(options['extra-mapping'], {}),
      home: options.home ?? '',
      exists: existsSync,
      credentialHelper: options['credential-helper'] ?? '',
      now,
    });
    const out = options['out-dir'];
    writeFileSync(path.join(out, 'inventory.json'), JSON.stringify(inventory, null, 2) + '\n');
    writeFileSync(path.join(out, 'path-map.json'), JSON.stringify(inventory.mapping, null, 2) + '\n');
    writeFileSync(path.join(out, 'checkouts.tsv'), inventory.checkouts.map(item => `${item.name}\t${item.repositoryUrl ?? ''}\n`).join(''));
    write(formatInventory(inventory) + '\n');
    return inventory.issues.length ? 3 : 0;
  }
  if (command === 'delta') {
    const inventory = readJson(options.inventory, null);
    if (!inventory) throw new Error('delta needs --inventory');
    const delta = selectDelta({ entries: inventory.entries, previous: readJson(options.previous, null) });
    writeFileSync(options.copy, delta.copy.map(item => item + '\0').join(''));
    writeFileSync(options.remove, delta.remove.map(item => item + '\0').join(''));
    writeFileSync(options.manifest, JSON.stringify({ generatedAt: now().toISOString(), ...delta.manifest }) + '\n');
    write(`Delta: ${delta.copy.length} to copy, ${delta.remove.length} to remove, ${delta.unchanged} unchanged\n`);
    return 0;
  }
  throw new Error('Usage: move-studio-to-server.mjs projects|inventory|delta [--option value]...');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    process.exitCode = runCli(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
