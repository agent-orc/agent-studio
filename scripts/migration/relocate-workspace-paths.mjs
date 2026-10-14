#!/usr/bin/env node
// Offline migration only: stop the API before applying changes to its store.
import { readFile, readdir, mkdir, copyFile, writeFile, rename, stat } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const windowsPath = /^(?:[a-z]:[\\/]|\\\\)/i;
const absolutePath = value => windowsPath.test(value) || value.startsWith('/');
const normalized = value => {
  const converted = value.replaceAll('\\', '/');
  return converted.replace(/\/+$/, '') || (converted.startsWith('/') ? '/' : converted);
};

export function relocate(value, mappings) {
  if (!windowsPath.test(value)) return { target: value, unresolved: false };
  const source = normalized(value).toLowerCase();
  for (const [from, to] of mappings) {
    const prefix = normalized(from).toLowerCase();
    if (source === prefix || source.startsWith(prefix + '/')) {
      const suffix = normalized(value).slice(prefix.length);
      return { target: suffix ? to.replace(/\/+$/, '') + suffix : to, unresolved: false };
    }
  }
  return { target: null, unresolved: true };
}

async function walkJson(dir, found = []) {
  for (const item of await readdir(dir, { withFileTypes: true })) {
    const file = path.join(dir, item.name);
    if (item.isDirectory()) await walkJson(file, found);
    else if (item.isFile() && item.name.endsWith('.json')) found.push(file);
  }
  return found;
}

async function walkLinks(root, found = []) {
  for (const item of await readdir(root, { withFileTypes: true })) {
    if (['node_modules', 'bin', 'obj', 'objects', 'logs', 'refs', 'hooks', '.migration-backups'].includes(item.name)) continue;
    const file = path.join(root, item.name);
    if (item.isDirectory()) await walkLinks(file, found);
    else if (item.isFile() && (item.name === '.git' || item.name === 'gitdir' || item.name === 'commondir'
      || item.name === 'config.worktree' || (item.name === 'config' && path.basename(root) === '.git'))) found.push(file);
  }
  return found;
}

export async function inventory({ workspace, config = [], checkout = [], link = [], mapping, apply = false }) {
  const raw = JSON.parse(await readFile(mapping, 'utf8'));
  if (!raw || Array.isArray(raw) || typeof raw !== 'object') throw new Error('Mapping must be a JSON object');
  const mappings = Object.entries(raw).map(([from, to]) => {
    if (!windowsPath.test(from) || typeof to !== 'string' || !to.startsWith('/') || windowsPath.test(to))
      throw new Error(`Invalid mapping: ${from}`);
    return [from, normalized(to)];
  }).sort((a, b) => b[0].length - a[0].length);
  const metadata = path.join(workspace, '.metadata');
  const files = [...await walkJson(metadata), ...config, ...link];
  for (const root of checkout) files.push(...await walkLinks(root));
  const unique = [...new Set(files.map(file => path.resolve(file)))].sort();
  const entries = [];
  const changes = [];
  for (const file of unique) {
    const original = await readFile(file, 'utf8');
    const json = file.endsWith('.json');
    if (json) JSON.parse(original);
    const token = json ? /"(?:\\.|[^"\\])*"/g : /[^\r\n]+/g;
    const updated = original.replace(token, match => {
      let value;
      const prefix = json ? '' : (match.match(/^(?:\s*gitdir:\s*|\s*[A-Za-z.]+\s*=\s*)/)?.[0] ?? '');
      try { value = json ? JSON.parse(match) : match.slice(prefix.length); }
      catch { return match; }
      if (typeof value !== 'string' || !absolutePath(value)) return match;
      const result = relocate(value, mappings);
      entries.push({ file, source: value, target: result.target, unresolved: result.unresolved });
      if (result.unresolved || result.target === value) return match;
      return json ? JSON.stringify(result.target) : prefix + result.target;
    });
    if (updated !== original) changes.push({ file, updated });
  }
  const unresolved = entries.filter(entry => entry.unresolved);
  if (apply && unresolved.length) throw new Error(`Refusing apply: ${unresolved.length} unresolved Windows path(s)`);
  let backup = null;
  if (apply && changes.length) {
    backup = path.join(path.dirname(workspace), `${path.basename(workspace)}.migration-backups`, new Date().toISOString().replaceAll(':', '-'));
    await mkdir(backup, { recursive: true, mode: 0o700 });
    const manifest = [];
    for (const [index, change] of changes.entries()) {
      const saved = path.join(backup, `${index}.bak`);
      await copyFile(change.file, saved);
      manifest.push({ source: change.file, backup: saved });
    }
    await writeFile(path.join(backup, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
    for (const change of changes) {
      const mode = (await stat(change.file)).mode;
      const temp = `${change.file}.relocate-${process.pid}`;
      await writeFile(temp, change.updated, { mode });
      await rename(temp, change.file);
    }
    // Re-read all source files, including files with no planned change.
    for (const file of unique) {
      const content = await readFile(file, 'utf8');
      if (/"[a-z]:[\\/]|(?:gitdir:|=)\s*[a-z]:[\\/]|^\\\\/im.test(content))
        throw new Error(`Windows path remains after apply: ${file}`);
    }
  }
  return { entries, unresolved: unresolved.length, changedFiles: changes.map(change => change.file), backup };
}

function parseArgs(args) {
  const result = { config: [], checkout: [], link: [], apply: false };
  for (let i = 0; i < args.length; i++) {
    const name = args[i].replace(/^--/, '');
    if (name === 'apply') { result.apply = true; continue; }
    if (!['workspace', 'mapping', 'config', 'checkout', 'link'].includes(name) || !args[i].startsWith('--') || !args[i + 1])
      throw new Error(`Unknown or incomplete option: ${args[i]}`);
    if (Array.isArray(result[name])) result[name].push(args[++i]);
    else result[name] = args[++i];
  }
  if (!result.workspace || !result.mapping) throw new Error('Usage: relocate-workspace-paths.mjs --workspace DIR --mapping FILE [--config FILE] [--checkout DIR] [--link FILE] [--apply]');
  return result;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const report = await inventory(parseArgs(process.argv.slice(2)));
    console.log(JSON.stringify(report, null, 2));
    if (report.unresolved) process.exitCode = 2;
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
