#!/usr/bin/env node

// Extract every versioned HTTP route registered by the standalone Task
// Server. build-inventory.mjs compares a frontend operation's target route to
// this source-derived surface, so v1Status describes backend availability and
// not merely whether Angular has already switched to the versioned URL.

import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(import.meta.dirname, '../..');
const taskServerDir = path.join(root, 'task-server');

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return walk(target);
    return entry.isFile() && entry.name.endsWith('.cs') && !entry.name.endsWith('.Tests.cs')
      ? [target]
      : [];
  });
}

const methodPattern = /\b([A-Za-z_][A-Za-z0-9_]*)\.Map(Get|Post|Put|Delete|Patch)\(\s*"([^"]*)"/g;
const groupPattern = /\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)\.MapGroup\(\s*"([^"]*)"\s*\)/g;

// MapHub is not a Map{Verb} call. Include the versioned Studio hub explicitly
// so the frontend WS operation is checked against the real Task Server path.
const routes = [{ method: 'WS', path: '/hubs/v1/studio', evidence: ['task-server/Program.cs'] }];
for (const file of walk(taskServerDir).sort()) {
  const source = fs.readFileSync(file, 'utf8');
  const relative = path.relative(root, file).replaceAll(path.sep, '/');
  const prefixes = { app: '' };
  let match;

  groupPattern.lastIndex = 0;
  while ((match = groupPattern.exec(source))) {
    const [, name, base, suffix] = match;
    if (!(base in prefixes)) continue;
    prefixes[name] = `${prefixes[base]}${suffix}`;
  }

  methodPattern.lastIndex = 0;
  while ((match = methodPattern.exec(source))) {
    const [, receiver, verb, suffix] = match;
    if (!(receiver in prefixes)) continue;
    const line = source.slice(0, match.index).split('\n').length;
    routes.push({
      method: verb.toUpperCase(),
      path: `${prefixes[receiver]}${suffix}` || '/',
      evidence: [`${relative}:${line}`],
    });
  }
}

const unique = new Map();
for (const route of routes.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method))) {
  const key = `${route.method} ${route.path}`;
  const current = unique.get(key) ?? { method: route.method, path: route.path, evidence: [] };
  for (const item of route.evidence) {
    if (!current.evidence.includes(item)) current.evidence.push(item);
  }
  unique.set(key, current);
}

process.stdout.write(`${JSON.stringify({ count: unique.size, routes: [...unique.values()] }, null, 2)}\n`);
