import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { buildInventory } from './build-inventory.mjs';

const dossier = path.dirname(fileURLToPath(import.meta.url));
const extractRoutesPath = path.join(dossier, 'extract-routes.mjs');
const routesJsonPath = path.join(dossier, 'routes.json');

test('routes.json is the exact output of regenerating from current frontend and backend source', () => {
  // Guards against a frontend HTTP call (or backend MapGroup) landing without
  // updating the inventory: if this fails, run
  // `node docs/studio-route-ownership/build-inventory.mjs --write` and commit
  // the result.
  const candidatePayload = JSON.parse(execFileSync(
    process.execPath,
    [extractRoutesPath],
    { encoding: 'utf8' },
  ));
  const fresh = buildInventory(candidatePayload);
  const committed = JSON.parse(fs.readFileSync(routesJsonPath, 'utf8'));
  assert.deepEqual(fresh, committed);
});

test('every frontend operation extract-routes.mjs can see is present in routes.json', () => {
  const extracted = JSON.parse(execFileSync(
    process.execPath,
    [extractRoutesPath],
    { encoding: 'utf8' },
  ));
  const committed = JSON.parse(fs.readFileSync(routesJsonPath, 'utf8'));
  const inventoryEvidence = new Set(committed.frontendRoutes.flatMap((route) => route.frontendEvidence));
  const missing = extracted.routes
    .flatMap((route) => route.evidence)
    .filter((evidence) => !inventoryEvidence.has(evidence));
  assert.deepEqual(missing, [], 'frontend call sites missing from routes.json frontendEvidence');
});
