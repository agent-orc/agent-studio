import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { openDeviceKey, certifySession, MemoryKeyProvider, derToP1363 } from '../src/device-key.mjs';
import { keyThumbprint } from '../src/agent.mjs';

test('missing hardware and failing hardware signing both fall back with a clear memory note', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'device-provider-'));
  try {
    for (const Provider of [class { constructor() { throw new Error('Absent'); } },
      class extends MemoryKeyProvider { sign() { throw new Error('Unavailable'); } }]) {
      const { provider, note } = openDeviceKey(dir, { platform: 'fake', hardwareDeviceKeys: { fake: true }, providers: { fake: Provider } });
      assert.equal(provider.bindingKind, 'memory');
      assert.match(note, /Hardware key unavailable/);
      assert.equal(certifySession(provider, 'session', 'signin'), null);
    }
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a provider certifies the session thumbprint, sign-in id and time with a domain-separated signature', () => {
  const provider = new MemoryKeyProvider();
  provider.bindingKind = 'tpm';
  const certification = certifySession(provider, 'session-thumb', 'signin-123', 42);
  const statement = Buffer.from('studio-op-device-certification-v1\nsession-thumb\nsignin-123\n42');
  assert.ok(crypto.verify('sha256', statement, { key: crypto.createPublicKey({ key: certification.publicKey, format: 'jwk' }),
    dsaEncoding: 'ieee-p1363' }, Buffer.from(certification.signature, 'base64url')));
  assert.equal(certification.bindingKind, 'tpm');
});

test('copying device configuration to a second fake machine does not copy a usable signing key', () => {
  const original = new MemoryKeyProvider();
  const otherMachine = new MemoryKeyProvider();
  const copied = JSON.parse(JSON.stringify({ publicJwk: original.publicJwk, wrappedKey: 'opaque-device-blob' }));
  const FakeProvider = class {
    bindingKind = 'tpm'; publicJwk = copied.publicJwk;
    sign(data) { return otherMachine.sign(data); }
  };
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'device-copy-'));
  try {
    const result = openDeviceKey(dir, { platform: 'fake', hardwareDeviceKeys: { fake: true }, providers: { fake: FakeProvider } });
    assert.equal(result.provider.bindingKind, 'memory');
    assert.notEqual(keyThumbprint(result.provider.publicJwk), keyThumbprint(original.publicJwk));
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('TPM DER signatures convert to the P1363 wire format', () => {
  const { privateKey, publicKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const data = crypto.randomBytes(32);
  const signature = derToP1363(crypto.sign('sha256', data, privateKey));
  assert.equal(signature.length, 64);
  assert.ok(crypto.verify('sha256', data, { key: publicKey, dsaEncoding: 'ieee-p1363' }, signature));
});

for (const platform of ['win32', 'darwin', 'linux']) {
  test(`${platform} hardware is disabled unless that platform is explicitly enabled`, () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'device-opt-in-'));
    let opened = 0;
    class FakeProvider extends MemoryKeyProvider {
      constructor() { super(); opened++; this.bindingKind = platform === 'darwin' ? 'secure-enclave' : 'tpm'; }
    }
    try {
      for (const hardwareDeviceKeys of [{}, { other: true }, { [platform]: 'true' }, { [platform]: false }]) {
        const result = openDeviceKey(dir, { platform, hardwareDeviceKeys, providers: { [platform]: FakeProvider } });
        assert.equal(result.provider.bindingKind, 'memory');
        assert.match(result.note, /off by default and not yet verified on real hardware/);
      }
      assert.equal(opened, 0);
      const result = openDeviceKey(dir, { platform, hardwareDeviceKeys: { [platform]: true }, providers: { [platform]: FakeProvider } });
      assert.equal(opened, 1);
      assert.equal(result.provider.bindingKind, platform === 'darwin' ? 'secure-enclave' : 'tpm');
      assert.match(result.note, /not yet verified on real hardware/);
    } finally { fs.rmSync(dir, { recursive: true, force: true }); }
  });
}
