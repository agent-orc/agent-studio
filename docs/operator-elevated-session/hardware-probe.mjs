// Manual only. Never invoked by the Linux unit suite.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import fs from 'node:fs';
import { WindowsTpmKeyProvider, SecureEnclaveKeyProvider, certifySession } from '../../tools/studio-op/src/device-key.mjs';
import { keyThumbprint } from '../../tools/studio-op/src/agent.mjs';

let failed = false;
function step(name, action) {
  try { action(); console.log(`PASS ${name}`); }
  catch (error) { failed = true; console.log(`FAIL ${name}: ${error.message}`); }
}
const configDir = process.argv[2] ?? path.join(os.homedir(), '.config', 'agent-studio');
const originalThumbprint = process.argv[3];
const Provider = { win32: WindowsTpmKeyProvider, darwin: SecureEnclaveKeyProvider }[process.platform];
let provider;
step('platform hardware and private export restriction', () => {
  assert.ok(Provider, 'Run on Windows TPM or macOS Secure Enclave.');
  fs.mkdirSync(configDir, { recursive: true, mode: 0o700 });
  // Constructors invoke the native export restriction probe; no memory fallback.
  provider = new Provider(configDir);
});
step('persistent device key', () => {
  assert.ok(provider, 'Hardware provider did not open.');
  assert.equal(keyThumbprint(new Provider(configDir).publicJwk), keyThumbprint(provider.publicJwk));
});
step('session certification and binding kind', () => {
  assert.ok(provider, 'Hardware provider did not open.');
  assert.equal(provider.bindingKind, process.platform === 'darwin' ? 'secure-enclave' : 'tpm');
  const thumb = crypto.randomBytes(32).toString('base64url');
  const id = crypto.randomUUID();
  const cert = certifySession(provider, thumb, id);
  assert.ok(crypto.verify('sha256', Buffer.from(`studio-op-device-certification-v1\n${thumb}\n${id}\n${cert.issuedAt}`),
    { key: crypto.createPublicKey({ key: cert.publicKey, format: 'jwk' }), dsaEncoding: 'ieee-p1363' },
    Buffer.from(cert.signature, 'base64url')));
  console.log(`Binding: ${provider.bindingKind}; thumbprint: ${keyThumbprint(provider.publicJwk)}`);
});
if (originalThumbprint) step('copied configuration cannot certify as original device', () => {
  assert.ok(provider, 'Wrapped key refused or provider unavailable: verify platform setup separately before recording copy refusal.');
  assert.notEqual(keyThumbprint(provider.publicJwk), originalThumbprint);
});
console.log('Manual Studio login and light/dark checks are separate: follow hardware-probe.md.');
process.exitCode = failed ? 1 : 0;
