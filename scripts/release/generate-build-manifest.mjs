#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../..');
const args = Object.fromEntries(process.argv.slice(2).map((arg) => {
  const split = arg.indexOf('=');
  if (split < 0) throw new Error(`Expected --name=value, got ${arg}`);
  return [arg.slice(2, split), arg.slice(split + 1)];
}));

const tag = required('tag');
const version = required('version');
const output = resolve(root, args.output ?? 'build-manifest.json');
if (tag !== `v${version}`) fail(`Agent Studio tag/version mismatch: ${tag} vs ${version}`);

const commit = git('rev-parse', 'HEAD');
const exactTag = git('tag', '--points-at', 'HEAD').split(/\r?\n/).filter(Boolean);
if (!exactTag.includes(tag)) fail(`HEAD ${commit} is not tagged ${tag}`);
const dirty = git('status', '--porcelain').length > 0;
if (dirty && args['allow-dirty'] !== 'true') fail('Refusing to create a release manifest from a dirty checkout');

const packageJson = JSON.parse(readFileSync(resolve(root, 'frontend/package.json'), 'utf8'));
const npmLock = JSON.parse(readFileSync(resolve(root, 'frontend/package-lock.json'), 'utf8'));
const nugetLock = JSON.parse(readFileSync(resolve(root, 'backend/packages.lock.json'), 'utf8'));
const cacSpec = packageJson.dependencies?.['coding-agent-chat'];
if (!cacSpec || cacSpec.startsWith('file:')) {
  fail('Coding Agent Chat must be an immutable registry or tarball release, not file: dist');
}

const carLocked = Object.values(nugetLock.dependencies ?? {})
  .map((framework) => framework?.CodingAgentRunner)
  .find(Boolean);
if (!carLocked?.resolved || !carLocked?.contentHash) fail('CodingAgentRunner is missing from backend/packages.lock.json');
const cacLocked = npmLock.packages?.['node_modules/coding-agent-chat'];
if (!cacLocked?.version || !cacLocked?.integrity || String(cacLocked.resolved ?? '').startsWith('file:'))
  fail('Coding Agent Chat registry version/integrity is missing from frontend/package-lock.json');

const car = artifact('car', 'CodingAgentRunner', carLocked.resolved, `sha512-${carLocked.contentHash}`);
const cac = artifact('cac', 'coding-agent-chat', cacLocked.version, cacLocked.integrity);
if (cacSpec !== cac.version) fail(`Coding Agent Chat must be exact-pinned: package.json=${cacSpec}, lock=${cac.version}`);

const builtAt = new Date().toISOString();
const unsigned = { schemaVersion: 1, application: 'Agent Studio', tag, version, commit, dirty, builtAt, codingAgentRunner: car, codingAgentChat: cac };
const integrity = `sha256-${createHash('sha256').update(JSON.stringify(unsigned)).digest('hex')}`;
writeFileSync(output, `${JSON.stringify({ ...unsigned, integrity }, null, 2)}\n`, { flag: 'wx' });
process.stdout.write(`${output}\n`);

function artifact(prefix, name, lockedVersion, lockedIntegrity) {
  const value = { name, version: required(`${prefix}-version`), tag: required(`${prefix}-tag`), commit: required(`${prefix}-commit`), integrity: required(`${prefix}-integrity`) };
  if (value.tag !== `v${value.version}`) fail(`${name} tag/version mismatch: ${value.tag} vs ${value.version}`);
  if (!/^sha(256|512)-[A-Za-z0-9+/=_-]+$/.test(value.integrity)) fail(`${name} integrity is invalid`);
  if (value.version !== lockedVersion) fail(`${name} version mismatch: lock=${lockedVersion}, supplied=${value.version}`);
  if (value.integrity !== lockedIntegrity) fail(`${name} integrity mismatch: lock=${lockedIntegrity}, supplied=${value.integrity}`);
  return value;
}
function required(name) { const value = args[name]; if (!value) fail(`Missing --${name}=...`); return value; }
function git(...argv) { return execFileSync('git', argv, { cwd: root, encoding: 'utf8', windowsHide: true }).trim(); }
function fail(message) { process.stderr.write(`release-manifest: ${message}\n`); process.exit(2); }
