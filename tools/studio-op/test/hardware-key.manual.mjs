// Explicit platform evidence probe, intentionally outside the automatic *.test.mjs suite.
// Run as the agent OS user with the platform prerequisites from operator-agent.md.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import { openDeviceKey, certifySession } from '../src/device-key.mjs';
import { keyThumbprint } from '../src/agent.mjs';

const configDir = process.argv[2] ?? path.join(os.homedir(), '.config', 'agent-studio');
const { provider, note } = openDeviceKey(configDir);
console.log(note);
assert.notEqual(provider.bindingKind, 'memory', 'Hardware execution evidence requires an available hardware provider.');
const again = openDeviceKey(configDir).provider;
assert.equal(keyThumbprint(again.publicJwk), keyThumbprint(provider.publicJwk), 'Device key must persist across opens.');
const session = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' }).publicKey.export({ format: 'jwk' });
const signInId = crypto.randomUUID();
const thumb = keyThumbprint(session);
const certification = certifySession(provider, thumb, signInId);
assert.ok(crypto.verify('sha256', Buffer.from(`studio-op-device-certification-v1\n${thumb}\n${signInId}\n${certification.issuedAt}`),
  { key: crypto.createPublicKey({ key: provider.publicJwk, format: 'jwk' }), dsaEncoding: 'ieee-p1363' }, Buffer.from(certification.signature, 'base64url')));
console.log(JSON.stringify({ platform: process.platform, bindingKind: provider.bindingKind, deviceThumbprint: keyThumbprint(provider.publicJwk),
  publicKey: provider.publicJwk, certification, signatureVerified: true, persistenceVerified: true,
  exportCheck: process.platform === 'darwin' ? 'SecureEnclave type exposes wrapped representation only; software scalar import refused'
    : 'conventional private export refused during provider initialization' }, null, 2));
