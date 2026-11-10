#!/usr/bin/env node
// Creates the Owner of a fresh local Studio for a product scenario run
// (local-demo.sh): bootstraps the Owner with the installer code, enrolls the
// second factor, and writes the sign-in file the scenario reads, mode 600.
// The password is generated here and leaves this process only in that file.
import { randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { parseArgs } from 'node:util';

import { SessionClient, totp } from './session.mjs';

const { values } = parseArgs({
  options: { 'studio-url': { type: 'string' }, 'bootstrap-code-file': { type: 'string' }, out: { type: 'string' } },
  strict: true,
});
if (!values['studio-url'] || !values['bootstrap-code-file'] || !values.out) {
  process.stderr.write('Usage: local-owner.mjs --studio-url URL --bootstrap-code-file PATH --out PATH\n');
  process.exit(2);
}

const bootstrapCode = readFileSync(values['bootstrap-code-file'], 'utf8').trim();
const login = 'product-owner';
const password = randomBytes(24).toString('base64url');
const client = new SessionClient({ url: values['studio-url'], clientId: 'product-scenario-local-owner' });

const created = await client.send('POST', '/api/auth/bootstrap', {
  body: { username: login, displayName: 'Olivia Owner', password, bootstrapCode },
});
if (created.status !== 200) fail(`POST /api/auth/bootstrap answered ${created.status} ${created.body?.error ?? ''}`);
client.csrfToken = created.body?.csrfToken ?? null;
const factor = await client.post('/api/auth/totp/start');
const enrolledAt = Date.now();
await client.post('/api/auth/totp/confirm', { code: totp(factor.secret, enrolledAt) });

writeFileSync(values.out, `${JSON.stringify({ login, password, totpSecret: factor.secret })}\n`, { mode: 0o600 });
// A code is accepted once; the scenario signs in with the code of a later window.
const nextWindow = 30_000 - (enrolledAt % 30_000) + 1_000;
await new Promise(resolve => setTimeout(resolve, nextWindow));
process.stdout.write(`local-owner: ${login} bootstrapped with a second factor\n`);

function fail(message) {
  process.stderr.write(`local-owner: ${message}\n`);
  process.exit(1);
}
