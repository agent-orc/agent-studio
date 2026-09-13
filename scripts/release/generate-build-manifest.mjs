#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';

const args = Object.fromEntries(process.argv.slice(2).map((arg) => {
  const split = arg.indexOf('=');
  if (split < 0) throw new Error(`Expected --name=value, got ${arg}`);
  return [arg.slice(2, split), arg.slice(split + 1)];
}));
const root = resolve(args.root ?? resolve(import.meta.dirname, '../..'));

const tag = required('tag');
const version = required('version');
const output = resolve(root, args.output ?? 'build-manifest.json');
if (tag !== `v${version}`) fail(`Agent Studio tag/version mismatch: ${tag} vs ${version}`);

const commit = git('rev-parse', 'HEAD');
const exactTag = git('tag', '--points-at', 'HEAD').split(/\r?\n/).filter(Boolean);
if (!exactTag.includes(tag)) fail(`HEAD ${commit} is not tagged ${tag}`);
const dirty = git('status', '--porcelain').length > 0;
if (dirty && args['allow-dirty'] !== 'true') fail('Refusing to create a release manifest from a dirty checkout');

const definitionPath = '.agent-studio/project.yml';
const release = parseReleaseDefinition(read(definitionPath));
if (release.identity.length === 0) fail(`${definitionPath} does not declare release.identity rules`);
if (release.restore.length === 0) fail(`${definitionPath} does not declare release.restore commands`);

const identities = new Map();
for (const rule of release.identity) {
  if (!rule.package) fail('release identity package is required');
  const key = rule.package.toLowerCase();
  if (identities.has(key)) fail(`release identity ${rule.package} is declared more than once`);
  identities.set(key, resolveIdentity(rule));
}
const carLocked = identities.get('codingagentrunner');
const cacLocked = identities.get('coding-agent-chat');
if (!carLocked) fail(`${definitionPath} does not declare release identity for CodingAgentRunner`);
if (!cacLocked) fail(`${definitionPath} does not declare release identity for coding-agent-chat`);

const car = artifact('car', 'CodingAgentRunner', carLocked.version, carLocked.integrity);
const cac = artifact('cac', 'coding-agent-chat', cacLocked.version, cacLocked.integrity);
const builtAt = new Date().toISOString();
const unsigned = { schemaVersion: 1, application: 'Agent Studio', tag, version, commit, dirty, builtAt, codingAgentRunner: car, codingAgentChat: cac };
const integrity = `sha256-${createHash('sha256').update(JSON.stringify(unsigned)).digest('hex')}`;
writeFileSync(output, `${JSON.stringify({ ...unsigned, integrity }, null, 2)}\n`, { flag: 'wx' });
process.stdout.write(`${output}\n`);

function resolveIdentity(rule) {
  if (!['nuget', 'npm'].includes(rule.ecosystem))
    fail(`release identity ${rule.package} has unsupported ecosystem ${rule.ecosystem || '<missing>'}`);

  if (rule.source) {
    if (!safeRelativePath(rule.source))
      fail(`release identity ${rule.package} source must be a repository-relative path without traversal`);
    if (rule.version || rule.integrity)
      fail(`release identity ${rule.package} must use either source or version/integrity`);
    const lock = readJson(rule.source);
    if (rule.ecosystem === 'nuget') return nugetIdentity(rule, lock);
    return npmIdentity(rule, lock);
  }

  if (!validExactVersion(rule.version) || !validIntegrity(rule.integrity))
    fail(`release identity ${rule.package} must declare an exact version and registry integrity`);
  return { version: rule.version, integrity: rule.integrity };
}

function nugetIdentity(rule, lock) {
  const locked = Object.values(lock.dependencies ?? {})
    .map((framework) => framework?.[rule.package])
    .find(Boolean);
  if (!locked?.resolved || !locked?.contentHash)
    fail(`${rule.package} is missing version or contentHash from ${rule.source}`);
  return { version: locked.resolved, integrity: `sha512-${locked.contentHash}` };
}

function npmIdentity(rule, lock) {
  const locked = lock.packages?.[`node_modules/${rule.package}`];
  if (!locked?.version || !validIntegrity(locked.integrity)
      || !locked.resolved || String(locked.resolved).startsWith('file:'))
    fail(`${rule.package} registry version/integrity is missing from ${rule.source}`);

  const packageJsonPath = resolve(dirname(resolve(root, rule.source)), 'package.json');
  let packageJson;
  try { packageJson = JSON.parse(readFileSync(packageJsonPath, 'utf8')); }
  catch (error) { fail(`${packageJsonPath} is invalid: ${error.message}`); }
  const spec = packageJson.dependencies?.[rule.package];
  if (!spec || spec.startsWith('file:'))
    fail(`${rule.package} must be an immutable registry release, not file: dist`);
  if (spec !== locked.version)
    fail(`${rule.package} must be exact-pinned: package.json=${spec}, lock=${locked.version}`);
  return { version: locked.version, integrity: locked.integrity };
}

function parseReleaseDefinition(yaml) {
  const lines = yaml.replace(/\r\n/g, '\n').split('\n').map((raw) => {
    const text = stripYamlComment(raw).trimEnd();
    return { indent: text.length - text.trimStart().length, text: text.trim() };
  }).filter((line) => line.text);
  const releaseIndex = lines.findIndex((line) => line.indent === 0 && line.text === 'release:');
  if (releaseIndex < 0) fail('.agent-studio/project.yml does not declare a release contract');
  const releaseLines = lines.slice(releaseIndex + 1);
  const end = releaseLines.findIndex((line) => line.indent === 0);
  const section = end < 0 ? releaseLines : releaseLines.slice(0, end);
  const identityIndex = section.findIndex((line) => line.indent === 2 && line.text === 'identity:');
  const restoreIndex = section.findIndex((line) => line.indent === 2 && line.text === 'restore:');
  const identity = [];
  if (identityIndex >= 0) {
    let current;
    for (const line of section.slice(identityIndex + 1)) {
      if (line.indent <= 2) break;
      if (line.indent === 4 && line.text.startsWith('- ')) {
        if (current) identity.push(current);
        current = {};
        assignYamlField(current, line.text.slice(2));
      } else if (line.indent === 6 && current) {
        assignYamlField(current, line.text);
      }
    }
    if (current) identity.push(current);
  }
  const restore = restoreIndex < 0 ? [] : readIndentedList(section, restoreIndex, 4);
  return { identity, restore };
}

function readIndentedList(lines, start, indent) {
  const result = [];
  for (const line of lines.slice(start + 1)) {
    if (line.indent < indent) break;
    if (line.indent === indent && line.text.startsWith('- ')) result.push(unquote(line.text.slice(2)));
  }
  return result;
}

function assignYamlField(target, text) {
  const split = text.indexOf(':');
  if (split < 0) fail(`invalid release identity field: ${text}`);
  target[text.slice(0, split).trim()] = unquote(text.slice(split + 1));
}

function unquote(value) {
  const trimmed = value.trim();
  if (trimmed.length >= 2 && ((trimmed.startsWith('"') && trimmed.endsWith('"'))
      || (trimmed.startsWith("'") && trimmed.endsWith("'")))) return trimmed.slice(1, -1);
  return trimmed;
}

function stripYamlComment(value) {
  let quote = '';
  for (let index = 0; index < value.length; index += 1) {
    const ch = value[index];
    if (!quote && (ch === '"' || ch === "'")) quote = ch;
    else if (quote === ch) quote = '';
    else if (!quote && ch === '#' && (index === 0 || /\s/.test(value[index - 1]))) return value.slice(0, index);
  }
  return value;
}

function artifact(prefix, name, lockedVersion, lockedIntegrity) {
  const value = { name, version: required(`${prefix}-version`), tag: required(`${prefix}-tag`), commit: required(`${prefix}-commit`), integrity: required(`${prefix}-integrity`) };
  if (value.tag !== `v${value.version}`) fail(`${name} tag/version mismatch: ${value.tag} vs ${value.version}`);
  if (!validIntegrity(value.integrity)) fail(`${name} integrity is invalid`);
  if (value.version !== lockedVersion) fail(`${name} version mismatch: rule=${lockedVersion}, supplied=${value.version}`);
  if (value.integrity !== lockedIntegrity) fail(`${name} integrity mismatch: rule=${lockedIntegrity}, supplied=${value.integrity}`);
  return value;
}
function validIntegrity(value) { return /^sha(256|512)-[A-Za-z0-9+/=_-]+$/.test(value ?? ''); }
function validExactVersion(value) { return /^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(value ?? ''); }
function safeRelativePath(value) { return !/^(?:[~/\\]|[A-Za-z]:)/.test(value) && !value.includes('\\') && !value.split('/').some((part) => part === '..' || part === ''); }
function read(path) { return readFileSync(resolve(root, path), 'utf8'); }
function readJson(path) {
  try { return JSON.parse(read(path)); }
  catch (error) { fail(`${path} is invalid: ${error.message}`); }
}
function required(name) { const value = args[name]; if (!value) fail(`Missing --${name}=...`); return value; }
function git(...argv) { return execFileSync('git', argv, { cwd: root, encoding: 'utf8', windowsHide: true }).trim(); }
function fail(message) { process.stderr.write(`release-manifest: ${message}\n`); process.exit(2); }
