#!/usr/bin/env node

// Extracts every /api/v1/... and /hubs/v1/... HTTP route actually registered
// in task-server/*.cs, by resolving MapGroup("prefix") variable chains and
// the Map{Get,Post,Put,Delete,Patch}("suffix", ...) calls nested under them.
// This is the ground truth for "does the standalone Task Server already
// implement this operation", used by build-inventory.mjs to mark a frontend
// route's v1Status as 'exists' instead of 'must-add'.

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

// app.MapHub<TaskServerStudioHub>("/hubs/v1/studio") in Program.cs is not a
// Map{Verb}(...) call, so the regex above cannot see it; the frontend's one
// WS operation (/hubs/jobs, target /hubs/v1/studio) needs this to resolve.
const routes = [{ method: 'WS', path: '/hubs/v1/studio', evidence: 'task-server/Program.cs' }];
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
    const fullPath = `${prefixes[receiver]}${suffix}` || '/';
    routes.push({
      method: verb.toUpperCase(),
      path: fullPath,
      evidence: `${relative}:${line}`,
    });
  }
}

const unique = new Map();
for (const route of routes.sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method))) {
  const key = `${route.method} ${route.path}`;
  const current = unique.get(key) ?? { method: route.method, path: route.path, evidence: [] };
  current.evidence.push(route.evidence);
  unique.set(key, current);
}

const payload = { count: unique.size, routes: [...unique.values()] };
const output = `${JSON.stringify(payload, null, 2)}\n`;
if (process.argv.includes('--write')) fs.writeFileSync(path.join(import.meta.dirname, 'taskserver-routes.json'), output);
else process.stdout.write(output);
