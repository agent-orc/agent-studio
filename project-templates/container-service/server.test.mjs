import assert from 'node:assert/strict';
import test from 'node:test';
import { respond } from './server.mjs';

test('health endpoint reports ready', () => {
  const result = { status: 0, body: '' };
  respond({ url: '/health' }, {
    writeHead: (status) => { result.status = status; },
    end: (body) => { result.body = body; },
  });
  assert.equal(result.status, 200);
  assert.deepEqual(JSON.parse(result.body), { status: 'ok' });
});
