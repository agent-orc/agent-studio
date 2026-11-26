import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { keyThumbprint } from './agent.mjs';

const native = fileURLToPath(new URL('../native/', import.meta.url));
const statement = (thumbprint, signInId, issuedAt) =>
  `studio-op-device-certification-v1\n${thumbprint}\n${signInId}\n${issuedAt}`;

/**
 * @typedef {object} DeviceKeyProvider
 * @property {'tpm'|'secure-enclave'|'memory'} bindingKind
 * @property {crypto.JsonWebKey} publicJwk Public EC P-256 JWK, never a private scalar.
 * @property {(data: Buffer) => Buffer} sign SHA-256 signing, returning an IEEE P1363 signature.
 */
// Hardware providers expose only public data, wrapped blobs and signatures.
export class MemoryKeyProvider {
  bindingKind = 'memory';
  constructor() {
    const keys = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
    this.key = keys.privateKey;
    this.publicJwk = keys.publicKey.export({ format: 'jwk' });
  }
  sign(data) { return crypto.sign('sha256', data, { key: this.key, dsaEncoding: 'ieee-p1363' }); }
}

function run(command, args, input) {
  const result = spawnSync(command, args, { input, timeout: 15_000, maxBuffer: 64 * 1024, windowsHide: true });
  if (result.error || result.status !== 0) throw new Error('Hardware provider unavailable or key cannot be opened.');
  return result.stdout;
}

class HelperKeyProvider {
  constructor(bindingKind, command, args, configDir) {
    this.bindingKind = bindingKind;
    this.command = command;
    this.args = [...args, configDir];
    const answer = JSON.parse(run(command, [...this.args, 'public']).toString());
    this.publicJwk = answer.publicJwk;
    if (answer.nonExportable !== true) throw new Error('Provider did not prove export refusal.');
  }
  sign(data) {
    return Buffer.from(JSON.parse(run(this.command, [...this.args, 'sign'], data.toString('base64')).toString()).signature, 'base64');
  }
}

export class WindowsTpmKeyProvider extends HelperKeyProvider {
  constructor(configDir) {
    super('tpm', 'powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File',
      path.join(native, 'device-key.ps1')], configDir);
  }
}

export class SecureEnclaveKeyProvider extends HelperKeyProvider {
  constructor(configDir) {
    // Install a compiled, consistently signed helper beside its source. Do not compile at login.
    super('secure-enclave', path.join(native, 'device-key-macos'), [], configDir);
  }
}

export class LinuxTpmKeyProvider {
  bindingKind = 'tpm';
  constructor(configDir) {
    this.file = path.join(configDir, 'device-key.tss');
    const providers = ['-provider', 'tpm2', '-provider', 'default'];
    this.providers = providers;
    if (!fs.existsSync(this.file)) {
      const wrapped = run('openssl', ['genpkey', ...providers, '-algorithm', 'EC', '-pkeyopt', 'group:P-256']);
      // The TSS2 blob is bound to the TPM's storage parent, never a private scalar.
      fs.writeFileSync(this.file, wrapped, { mode: 0o600, flag: 'wx' });
    }
    fs.chmodSync(this.file, 0o600);
    const pem = run('openssl', ['pkey', ...providers, '-in', this.file, '-pubout']);
    this.publicJwk = crypto.createPublicKey(pem).export({ format: 'jwk' });
    // A conventional EC private-key export must be refused by the TPM provider.
    const exported = spawnSync('openssl', ['pkey', ...providers, '-in', this.file, '-traditional'],
      { timeout: 15_000, maxBuffer: 64 * 1024, windowsHide: true });
    if (exported.error || exported.status === 0) throw new Error('TPM export refusal could not be established.');
  }
  sign(data) {
    const der = run('openssl', ['dgst', ...this.providers, '-sha256', '-sign', this.file], data);
    return derToP1363(der);
  }
}

export function derToP1363(der) {
  // P-256 ECDSA DER sequence, two positive integers of at most 33 bytes.
  if (der[0] !== 0x30 || der[1] !== der.length - 2) throw new Error('Invalid signature.');
  let offset = 2;
  const values = [];
  for (let index = 0; index < 2; index++) {
    if (der[offset++] !== 2) throw new Error('Invalid signature.');
    const size = der[offset++];
    let value = der.subarray(offset, offset + size); offset += size;
    if (value.length === 33 && value[0] === 0) value = value.subarray(1);
    if (!size || value.length > 32) throw new Error('Invalid signature.');
    values.push(Buffer.concat([Buffer.alloc(32 - value.length), value]));
  }
  if (offset !== der.length) throw new Error('Invalid signature.');
  return Buffer.concat(values);
}

export function openDeviceKey(configDir, { platform = process.platform, providers = {
  win32: WindowsTpmKeyProvider, darwin: SecureEnclaveKeyProvider, linux: LinuxTpmKeyProvider,
} } = {}) {
  fs.mkdirSync(configDir, { recursive: true, mode: 0o700 });
  try {
    const Provider = providers[platform];
    if (!Provider) throw new Error('No platform provider.');
    const provider = new Provider(configDir);
    // Test actual signing before advertising hardware binding.
    const probe = crypto.randomBytes(32);
    if (!crypto.verify('sha256', probe, { key: crypto.createPublicKey({ key: provider.publicJwk, format: 'jwk' }),
      dsaEncoding: 'ieee-p1363' }, provider.sign(probe))) throw new Error('Provider signature failed.');
    return { provider, note: `Device binding: ${provider.bindingKind} (${keyThumbprint(provider.publicJwk)}).` };
  } catch {
    return { provider: new MemoryKeyProvider(), note:
      'Device binding: memory. Hardware key unavailable, inaccessible or unusable; using the resident session key. See the operator agent setup guide.' };
  }
}

export function certifySession(provider, sessionThumbprint, signInId, issuedAt = Math.floor(Date.now() / 1000)) {
  if (provider.bindingKind === 'memory') return null;
  return { publicKey: provider.publicJwk, bindingKind: provider.bindingKind, issuedAt,
    signature: provider.sign(Buffer.from(statement(sessionThumbprint, signInId, issuedAt))).toString('base64url') };
}
