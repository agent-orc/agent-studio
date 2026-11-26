import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { agentCall, serveAgent, digest, keyThumbprint } from '../src/agent.mjs';

const decode = part => JSON.parse(Buffer.from(part, 'base64url'));
const verify = (jwk, data, signature) => crypto.verify('sha256', Buffer.from(data),
  { key: crypto.createPublicKey({ key: jwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(signature, 'base64url'));

for (const refused of [false, true]) test(`resident rotates with the retained device key or asks for login after refusal (${refused})`, async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-renewal-'));
  const token = ['oas', crypto.randomUUID(), crypto.randomBytes(32).toString('base64url')].join('.');
  fs.writeFileSync(path.join(root, 'operator-session'), token);
  let time = Date.now();
  let renewal;
  let settle;
  const observed = new Promise(resolve => { settle = resolve; });
  const resident = await serveAgent(root, () => {}, { now: () => time, monitorIntervalMs: 20,
    fetchImpl: async (url, options) => {
      if (String(url).endsWith('/renew')) {
        renewal = { body: JSON.parse(options.body), proof: options.headers.DPoP };
        settle();
        return Response.json(refused ? { error: 'renewal-device-key-missing' }
          : { keyExpiresAt: new Date(time + 3600_000).toISOString() }, { status: refused ? 401 : 200 });
      }
      return Response.json({ state: 'active' });
    } });
  try {
    await agentCall(resident.endpoint, { operation: 'attach', token, sessionId: 'session', baseUrl: 'https://studio.test/',
      expiresAt: new Date(time + 24 * 3600_000).toISOString(), keyExpiresAt: new Date(time + 3600_000).toISOString() });
    time += 50 * 60_000;
    await Promise.race([observed, new Promise((_, reject) => setTimeout(() => reject(new Error('No rotation.')), 3000).unref())]);
    assert.equal(renewal.body.sessionId, 'session');
    assert.notEqual(keyThumbprint(renewal.body.newPublicKey), resident.thumbprint);
    assert.ok(verify(resident.publicJwk, `studio-op-session-renewal-v1\nsession\n${keyThumbprint(renewal.body.newPublicKey)}\n${renewal.body.issuedAt}`, renewal.body.signature));
    const [h, p, signature] = renewal.proof.split('.');
    assert.deepEqual(decode(h).jwk, resident.publicJwk);
    assert.ok(verify(resident.publicJwk, `${h}.${p}`, signature));
    // Give the fetch continuation and key replacement one event-loop turn.
    await new Promise(resolve => setTimeout(resolve, 50));
    if (refused) {
      assert.match(fs.readFileSync(path.join(root, 'operator-session-ended'), 'utf8'), /studio-op login/);
      await assert.rejects(agentCall(resident.endpoint, { operation: 'sign', tokenHash: digest(token), method: 'GET', url: 'https://studio.test/api/tasks' }));
    } else {
      const { proof } = await agentCall(resident.endpoint, { operation: 'sign', tokenHash: digest(token), method: 'GET', url: 'https://studio.test/api/tasks' });
      assert.deepEqual(decode(proof.split('.')[0]).jwk, renewal.body.newPublicKey);
    }
  } finally { resident.close(); fs.rmSync(root, { recursive: true, force: true }); }
});
