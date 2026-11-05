// Run: node --test tools/studio-op/test/credential.test.mjs
//
// Guardrail "Tokens live only in files" (Dossier operator-agent-level): the
// token comes only from a file with owner-only permissions, and every output
// is redacted.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { CredentialError, readCredential, redact, refuseTokenCarriers } from '../src/credential.mjs';
import { fakeToken } from './fake-studio.mjs';

const posix = process.platform !== 'win32';

function home() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'studio-op-credential-'));
}

function writeToken(dir, name, value, mode = 0o600) {
  const folder = path.join(dir, '.config', 'agent-studio');
  fs.mkdirSync(folder, { recursive: true });
  const file = path.join(folder, name);
  fs.writeFileSync(file, value + '\n', { mode });
  fs.chmodSync(file, mode);
  return file;
}

test('reads the operator token file by default', () => {
  const dir = home();
  const token = fakeToken();
  writeToken(dir, 'operator-token', token);
  const credential = readCredential({ home: dir, config: {} });
  assert.equal(credential.token, token);
  assert.equal(credential.kind, 'api-token');
  assert.match(credential.file, /operator-token$/);
});

test('reads only the operator session file when the config names it, with no fallback', () => {
  const dir = home();
  writeToken(dir, 'operator-token', fakeToken());
  assert.throws(() => readCredential({ home: dir, config: { credential: 'operator-session' } }),
    error => error instanceof CredentialError && error.code === 'token-file-missing');
  const session = fakeToken('oas');
  writeToken(dir, 'operator-session', session);
  const credential = readCredential({ home: dir, config: { credential: 'operator-session' } });
  assert.equal(credential.token, session);
  assert.equal(credential.kind, 'operator-session');
});

test('refuses a credential name outside the two known files', () => {
  assert.throws(() => readCredential({ home: home(), config: { credential: '../../etc/passwd' } }),
    error => error.code === 'credential-name-invalid');
});

test('returns null when the file does not exist', () => {
  assert.equal(readCredential({ home: home(), config: {} }), null);
});

test('refuses a file readable by group or others', { skip: !posix }, () => {
  const dir = home();
  writeToken(dir, 'operator-token', fakeToken(), 0o640);
  assert.throws(() => readCredential({ home: dir, config: {} }),
    error => error.code === 'token-file-permissions' && /chmod 600/.test(error.message));
});

test('refuses a symbolic link', { skip: !posix }, () => {
  const dir = home();
  const target = writeToken(dir, 'elsewhere', fakeToken());
  fs.symlinkSync(target, path.join(dir, '.config', 'agent-studio', 'operator-token'));
  assert.throws(() => readCredential({ home: dir, config: {} }), error => error.code === 'token-file-not-regular');
});

test('refuses an empty file and a file with more than one value', () => {
  const dir = home();
  writeToken(dir, 'operator-token', '');
  assert.throws(() => readCredential({ home: dir, config: {} }), error => error.code === 'token-file-empty');
  writeToken(dir, 'operator-token', `${fakeToken()} ${fakeToken()}`);
  assert.throws(() => readCredential({ home: dir, config: {} }), error => error.code === 'token-file-invalid');
});

test('the token error messages never contain the token', { skip: !posix }, () => {
  const dir = home();
  const token = fakeToken();
  writeToken(dir, 'operator-token', token, 0o644);
  try { readCredential({ home: dir, config: {} }); assert.fail('expected a refusal'); }
  catch (error) { assert.ok(!error.message.includes(token)); }
});

test('refuses the retired environment variable and any token-shaped environment value', () => {
  const retired = ['AGENT', 'STUDIO', 'API', 'TOKEN'].join('_');
  assert.throws(() => refuseTokenCarriers({ env: { [retired]: 'x' }, argv: [] }),
    error => error.code === 'token-environment-refused');
  assert.throws(() => refuseTokenCarriers({ env: { SOME_VARIABLE: fakeToken() }, argv: [] }),
    error => error.code === 'token-environment-refused' && /SOME_VARIABLE/.test(error.message));
  assert.doesNotThrow(() => refuseTokenCarriers({ env: { HOME: '/home/demo' }, argv: ['triage'] }));
});

test('refuses a token option and a token-shaped argument', () => {
  assert.throws(() => refuseTokenCarriers({ env: {}, argv: ['triage', '--token', 'x'] }),
    error => error.code === 'token-argument-refused');
  assert.throws(() => refuseTokenCarriers({ env: {}, argv: ['triage', '--token=x'] }),
    error => error.code === 'token-argument-refused');
  assert.throws(() => refuseTokenCarriers({ env: {}, argv: ['log', 'append', '--text', `see ${fakeToken('sat')}`] }),
    error => error.code === 'token-argument-refused');
});

test('redacts the exact token, token-shaped values and bearer headers', () => {
  const token = fakeToken();
  const other = fakeToken('oas');
  const text = `token ${token} other ${other} header Authorization: Bearer abc.def and {"echo":"Bearer ${token}"}`;
  const redacted = redact(text, token);
  assert.ok(!redacted.includes(token));
  assert.ok(!redacted.includes(other));
  assert.ok(!redacted.includes('Bearer abc.def'));
  assert.match(redacted, /\[redacted-token\]/);
});

test('redaction leaves ordinary text, cursors and lease ids intact', () => {
  const text = 'api.example host, lease-3, cursor 42, refs/heads/agent-studio/results/run_x/fence-1/abc';
  assert.equal(redact(text, null), text);
});
