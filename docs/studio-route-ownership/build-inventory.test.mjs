import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { buildInventory, canonicalPath, splitQuery } from './build-inventory.mjs';

test('splitQuery treats a trailing {query} placeholder as a dynamic query suffix, not a path segment', () => {
  // frontend/src/app/features/watcher/services/watcher-api.service.ts builds
  // `${this.baseUrl}/cases${query}` where `query` is itself a conditional
  // `?state=...` string; extract-routes.mjs can only resolve the whole
  // interpolation to a placeholder named after the variable: `{query}`.
  const result = splitQuery(canonicalPath('/api/watcher/cases{query}'));
  assert.equal(result.path, '/api/watcher/cases');
  assert.deepEqual(result.fixed, {});
  assert.deepEqual(result.optional, []);
  assert.equal(result.dynamic, true);
});

test('splitQuery treats a trailing {query} placeholder as dynamic for another route sharing the same variable name', () => {
  const result = splitQuery(canonicalPath('/api/watcher/proposals{query}'));
  assert.equal(result.path, '/api/watcher/proposals');
  assert.equal(result.dynamic, true);
});

test('splitQuery treats "?${...}" as a dynamic query suffix instead of inventing a literal query key', () => {
  // global-search.service.ts builds `/api/search/stream?${params.toString()}`;
  // the interpolation resolves to the opaque placeholder `{toString}`, which
  // is not a literal `key=value` pair.
  const result = splitQuery(canonicalPath('/api/search/stream?{toString}'));
  assert.equal(result.path, '/api/search/stream');
  assert.deepEqual(result.fixed, {});
  assert.deepEqual(result.optional, []);
  assert.equal(result.dynamic, true);
});

test('the frontend extractor marker for a conditional query suffix stays out of the route path', () => {
  const result = splitQuery(canonicalPath('/api/crash-recovery/pending/{id}/commit{query}'));
  assert.equal(result.path, '/api/crash-recovery/pending/{id}/commit');
  assert.deepEqual(result.fixed, {});
  assert.deepEqual(result.optional, []);
  assert.equal(result.dynamic, true);
});

test('splitQuery still parses literal fixed and optional query keys unchanged', () => {
  const literal = splitQuery(canonicalPath('/api/tasks?project=demo'));
  assert.equal(literal.path, '/api/tasks');
  assert.deepEqual(literal.fixed, { project: 'demo' });
  assert.equal(literal.dynamic, false);

  const optional = splitQuery(canonicalPath('/api/tasks?limit={limit}'));
  assert.equal(optional.path, '/api/tasks');
  assert.deepEqual(optional.optional, ['limit']);
  assert.equal(optional.dynamic, false);
});

test('buildInventory emits clean paths and a dynamic query variant for the three malformed call shapes', () => {
  const emptyBackendRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'route-inventory-test-'));
  try {
    const payload = buildInventory(
      {
        routes: [
          { method: 'GET', path: '/api/watcher/cases{query}', evidence: ['frontend/src/app/features/watcher/services/watcher-api.service.ts:26'] },
          { method: 'GET', path: '/api/watcher/proposals{query}', evidence: ['frontend/src/app/features/watcher/services/watcher-api.service.ts:31'] },
          { method: 'GET', path: '/api/search/stream?{toString}', evidence: ['frontend/src/app/features/studio-shell/components/global-search/global-search.service.ts:100'] },
        ],
      },
      { backendRoot: emptyBackendRoot },
    );

    const byPath = Object.fromEntries(payload.frontendRoutes.map((route) => [`${route.method} ${route.path}`, route]));

    for (const key of ['GET /api/watcher/cases', 'GET /api/watcher/proposals', 'GET /api/search/stream']) {
      const route = byPath[key];
      assert.ok(route, `expected a clean route for ${key}`);
      assert.ok(!route.path.includes('{query}') && !route.path.includes('{toString}'), `path leaked a template placeholder: ${route.path}`);
      assert.deepEqual(
        route.queryVariants.find((variant) => variant.kind === 'fixed'),
        undefined,
        `${key} must not record a fake literal query key`,
      );
      assert.ok(
        route.queryVariants.some((variant) => variant.kind === 'dynamic'),
        `${key} should be flagged as a dynamic query instead of losing the query entirely`,
      );
    }
  } finally {
    fs.rmSync(emptyBackendRoot, { recursive: true, force: true });
  }
});

test('buildInventory marks the generated target as existing only when the standalone Task Server maps it', () => {
  const emptyBackendRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'route-inventory-v1-test-'));
  try {
    const payload = buildInventory(
      {
        routes: [
          { method: 'GET', path: '/api/tasks/{taskId}/core', evidence: ['frontend/src/app/services/task.service.ts:1'] },
          { method: 'GET', path: '/api/v1/management/links', evidence: ['frontend/src/app/services/task.service.ts:2'] },
        ],
      },
      {
        backendRoot: emptyBackendRoot,
        taskServerPayload: {
          routes: [
            { method: 'GET', path: '/api/v1/projects/{projectIdentity}/tasks/{taskIdentity}/core' },
          ],
        },
      },
    );

    const taskCore = payload.frontendRoutes.find(
      (route) => route.method === 'GET' && route.path === '/api/tasks/{taskId}/core',
    );
    assert.equal(taskCore.targetRoute, '/api/v1/projects/{projectId}/tasks/{taskId}/core');
    assert.equal(taskCore.v1Status, 'exists', 'parameter names must not affect a v1 route match');

    const links = payload.frontendRoutes.find(
      (route) => route.method === 'GET' && route.path === '/api/v1/management/links',
    );
    assert.equal(links.v1Status, 'must-add', 'a versioned frontend URL alone is not a Task Server implementation');
  } finally {
    fs.rmSync(emptyBackendRoot, { recursive: true, force: true });
  }
});
