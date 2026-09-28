import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { buildInventory } from './build-inventory.mjs';

// AGT-2983: the 27 P0 core-attach operations moved from the legacy /api
// surface onto their versioned Task Server routes. This pins both halves:
// none of the legacy method+path keys may come back, and every versioned key
// stays owned by an existing v1 route.

const dossier = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(dossier, '../..');

const legacyToVersioned = [
  ['POST /api/auth/bootstrap', 'POST /api/v1/studio/auth/bootstrap'],
  ['POST /api/auth/change-password', 'POST /api/v1/studio/auth/change-password'],
  ['POST /api/auth/login', 'POST /api/v1/studio/auth/login'],
  ['POST /api/auth/logout', 'POST /api/v1/studio/auth/logout'],
  ['GET /api/auth/status', 'GET /api/v1/studio/auth/status'],
  ['GET /api/tasks/grouped', 'GET /api/v1/studio/board'],
  ['GET /api/workspaces', 'GET /api/v1/workspaces'],
  ['POST /api/workspaces', 'POST /api/v1/workspaces'],
  ['GET /api/projects', 'GET /api/v1/projects'],
  ['POST /api/projects', 'POST /api/v1/projects'],
  ['GET /api/tasks/{taskId}', 'GET /api/v1/projects/{projectId}/tasks/{taskId}'],
  ['DELETE /api/tasks/{taskId}', 'DELETE /api/v1/projects/{projectId}/tasks/{taskId}'],
  ['POST /api/tasks/{taskId}/continue', 'POST /api/v1/projects/{projectId}/tasks/{taskId}/continue'],
  ['POST /api/tasks/{taskId}/move', 'POST /api/v1/projects/{projectId}/tasks/{taskId}/move'],
  ['POST /api/tasks/{taskId}/move-to-top', 'POST /api/v1/projects/{projectId}/tasks/{taskId}/move-to-top'],
  ['POST /api/tasks/{taskId}/start', 'POST /api/v1/projects/{projectId}/tasks/{taskId}/start'],
  ['PUT /api/tasks/{taskId}/state', 'PUT /api/v1/projects/{projectId}/tasks/{taskId}/state'],
  ['POST /api/tasks/{taskId}/stop', 'POST /api/v1/projects/{projectId}/tasks/{taskId}/stop'],
  ['GET /api/orchestrator/context/{contextKey}', 'GET /api/v1/studio/orchestrator/context/{contextKey}'],
  ['POST /api/orchestrator/context/{contextKey}/refresh', 'POST /api/v1/studio/orchestrator/context/{contextKey}/refresh'],
  ['GET /api/orchestrator/sessions', 'GET /api/v1/studio/orchestrator/sessions'],
  [
    'POST /api/orchestrator/sessions/workbench:{project}/{workbenchKey}/turns',
    'POST /api/v1/studio/orchestrator/sessions/workbench:{project}/{workbenchKey}/turns',
  ],
  ['GET /api/runner/{project}/orchestrator-chat', 'GET /api/v1/studio/runner/{project}/orchestrator-chat'],
  ['POST /api/runner/{project}/orchestrator-chat', 'POST /api/v1/studio/runner/{project}/orchestrator-chat'],
  [
    'GET /api/runner/{project}/orchestrator-chat/attachments/{fileName}',
    'GET /api/v1/studio/runner/{project}/orchestrator-chat/attachments/{fileName}',
  ],
  ['GET /api/runner/status', 'GET /api/v1/studio/runner/status'],
  ['WS /hubs/jobs', 'WS /hubs/v1/studio'],
];

function inventory() {
  const candidates = JSON.parse(execFileSync(
    process.execPath,
    [path.join(dossier, 'extract-routes.mjs')],
    { encoding: 'utf8' },
  ));
  return buildInventory(candidates);
}

test('the core-attach bundle lists exactly 27 operations', () => {
  assert.equal(legacyToVersioned.length, 27);
});

test('no Studio call site uses a legacy route of the 27 core-attach operations', () => {
  const keys = new Set(inventory().frontendRoutes.map((route) => `${route.method} ${route.path}`));
  const remaining = legacyToVersioned.map(([legacy]) => legacy).filter((key) => keys.has(key));
  assert.deepEqual(remaining, []);
});

test('every core-attach operation calls an existing versioned route', () => {
  const routes = new Map(inventory().frontendRoutes.map((route) => [`${route.method} ${route.path}`, route]));
  for (const [, versioned] of legacyToVersioned) {
    const route = routes.get(versioned);
    assert.ok(route, `${versioned} has no frontend call site`);
    assert.equal(route.v1Status, 'exists', versioned);
    assert.equal(route.targetRoute, route.path, versioned);
  }
  const coreAttach = inventory().d4bEstimate.bundles.find((bundle) => bundle.id === 'core-attach');
  assert.equal(coreAttach.routeCount, 0);
});

test('no URL helper outside HttpClient still builds a legacy core-attach path', () => {
  // extract-routes.mjs only sees HttpClient/fetch/SignalR call sites. URL
  // producers such as the chat attachment <img> resolver are listed by hand in
  // build-inventory.mjs, so scan the source text for the legacy literals too.
  const legacyLiterals = [
    // A comparison against a server-reported `loginUrl` is not a call site.
    /(?<!=== )['"`]\/api\/auth\/(?:bootstrap|change-password|login|logout|status)['"`]/,
    /['"`]\/hubs\/jobs['"`]/,
    /['"`]\/api\/tasks\/grouped/,
    /['"`]\/api\/runner\/status['"`]/,
    /['"`]\/api\/runner\/\$\{encodeURIComponent\(projectName\)\}\/orchestrator-chat/,
    /['"`]\/api\/orchestrator\/(?:sessions|context)/,
  ];
  const offenders = [];
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const target = path.join(directory, entry.name);
      if (entry.isDirectory()) walk(target);
      else if (entry.name.endsWith('.ts') && !entry.name.endsWith('.spec.ts')) {
        const source = fs.readFileSync(target, 'utf8')
          .replace(/\/\*[\s\S]*?\*\//g, '')
          .replace(/^\s*\/\/.*$/gm, '');
        for (const pattern of legacyLiterals) {
          if (pattern.test(source)) offenders.push(`${path.relative(root, target)} ${pattern}`);
        }
      }
    }
  };
  walk(path.join(root, 'frontend/src/app'));
  assert.deepEqual(offenders, []);
});
