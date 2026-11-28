import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { submitBatch } from '../src/batches.mjs';

for (const kind of ['accept', 'archive']) {
  test(`${kind} batch submits typed items and waits for Studio execution`, async () => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-batch-'));
    const file = path.join(folder, 'items.json');
    const calls = [];
    fs.writeFileSync(file, JSON.stringify([{ card: 'DEMO-1', reason: 'Owner requested decision', reservation: 'Small naming concern' }]));
    let polls = 0;
    try {
      const answer = await submitBatch({ url: 'https://studio.test', waitForBatchPoll: async () => {},
        client: { request: async (method, url, options) => {
          calls.push({ method, url, options });
          if (method === 'POST') return { id: 'opb_test', state: 'pending' };
          return ++polls === 1 ? { id: 'opb_test', state: 'executing' }
            : { id: 'opb_test', state: 'confirmed', items: [{ request: { card: 'DEMO-1' }, state: 'executed', resultReason: 'Done' }] };
        } } }, { batch: file, project: 'demo', source: 'owner-chat' }, kind);
      assert.equal(answer.data.state, 'confirmed');
      assert.equal(calls[0].options.body.class, `cards.${kind}-override`);
      assert.equal(calls[0].options.body.items[0].targetLane, kind === 'accept' ? '6-completed' : '7-archive');
      assert.equal(polls, 2);
      assert.equal(calls.filter(call => call.url.includes('/move')).length, 0);
    } finally { fs.rmSync(folder, { recursive: true, force: true }); }
  });
}
