import { test } from 'node:test';
import assert from 'node:assert/strict';
import { forbiddenImport, missingInputs } from './check-ui-library.mjs';

test('library imports stay inside the library regardless of relative spelling', () => {
  assert.equal(forbiddenImport('../services/overlay', '/ui/components/menu.ts', '/ui'), false);
  assert.equal(forbiddenImport('../../app/services/tasks', '/ui/components/menu.ts', '/ui'), true);
  assert.equal(forbiddenImport('coding-agent-chat/shared', '/ui/components/menu.ts', '/ui'), true);
  assert.equal(forbiddenImport('@angular/core', '/ui/components/menu.ts', '/ui'), false);
});
test('each catalogue state supplies required component inputs', () => {
  assert.deepEqual(missingInputs(['panelId'], [{ id: 'header', inputs: {} }]), ['header: missing required input panelId']);
  assert.deepEqual(missingInputs(['panelId'], [{ id: 'header', inputs: { panelId: 'records' } }]), []);
});
