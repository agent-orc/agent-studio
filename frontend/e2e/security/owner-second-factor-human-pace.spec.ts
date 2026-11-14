import { test, expect, type Page } from '@playwright/test';
import { createHmac } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { setTheme } from '../helpers/theme';

// AGT-3614: a fresh networked monolith behind the HTTPS test proxy (profile
// networked-https). The live-update hub stays disconnected while the sign-in
// gate is shown, so the offline guard turns on after four seconds. The owner
// must still be able to enroll a second factor at human pace.
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'owner-second-factor');

function base32Decode(secret: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let buffer = 0;
  let bits = 0;
  const bytes: number[] = [];
  for (const char of secret.replace(/=+$/, '').toUpperCase()) {
    buffer = (buffer << 5) | alphabet.indexOf(char);
    bits += 5;
    if (bits >= 8) { bits -= 8; bytes.push((buffer >> bits) & 255); }
  }
  return Buffer.from(bytes);
}

/** RFC 6238 SHA-1, six digits, 30 s steps: what an authenticator app shows. */
function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(Math.floor(at / 30_000)));
  const digest = createHmac('sha1', base32Decode(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 15;
  const value = digest.readUInt32BE(offset) & 0x7fffffff;
  return String(value % 1_000_000).padStart(6, '0');
}

/** A six-digit code that no step near now accepts. */
function wrongCode(secret: string): string {
  const near = new Set([-60_000, -30_000, 0, 30_000, 60_000].map(delta => totp(secret, Date.now() + delta)));
  for (let candidate = 0; ; candidate++) {
    const code = String(candidate).padStart(6, '0');
    if (!near.has(code)) return code;
  }
}

async function capture(page: Page, name: string): Promise<void> {
  const mask = [page.getByTestId('totp-secret'), page.getByTestId('totp-link'), page.getByTestId('totp-qr'),
    page.getByTestId('factor-recovery-codes'), page.getByLabel('Authenticator code')];
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, `${name}-light.png`), fullPage: true, mask });
  await setTheme(page, 'dark');
  await page.screenshot({ path: join(resultDir, `${name}-dark.png`), fullPage: true, mask });
  const viewport = page.viewportSize()!;
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: join(resultDir, `${name}-390.png`), fullPage: true, mask });
  await page.setViewportSize(viewport);
}

test('owner enrolls an authenticator after a human-length pause on the second-factor screen', async ({ page }) => {
  test.setTimeout(150_000);
  mkdirSync(resultDir, { recursive: true });
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace, 'run through npm run e2e so the networked-https stack is isolated').toBeTruthy();

  await page.goto('/');
  const gate = page.getByTestId('auth-gate');
  const alert = gate.getByRole('alert');
  await expect(gate.getByRole('heading', { name: 'Create the first owner' })).toBeVisible();
  await gate.getByLabel('Username').fill('owner');
  await gate.getByLabel('Display name').fill('Owner');
  await gate.getByLabel('Installer owner code').fill(`${basename(workspace!)}-owner-code`);
  await gate.getByLabel('Password').fill(['studio', 'setup', 'passphrase'].join('-'));
  await gate.getByRole('button', { name: 'Create owner' }).click();

  await expect(gate.getByRole('heading', { name: 'Set up a second factor' })).toBeVisible();
  // A human reads the instructions and reaches for a phone. The offline grace
  // window is four seconds; wait well past it before the first request.
  await page.waitForTimeout(30_000);
  await capture(page, 'second-factor-after-pause');

  await gate.getByRole('button', { name: 'Set up authenticator' }).click();
  const secretOutput = gate.getByTestId('totp-secret');
  await expect(secretOutput).toHaveText(/^[A-Z2-7]{32}$/);
  await expect(alert).toHaveCount(0);
  const secret = (await secretOutput.textContent())!.trim();

  // A request that never reaches the server names the connection, not the code.
  await page.route('**/api/auth/totp/confirm', route => route.abort('internetdisconnected'), { times: 1 });
  await gate.getByLabel('Authenticator code').fill(totp(secret));
  await gate.getByRole('button', { name: 'Verify authenticator' }).click();
  await expect(alert).toHaveText('Studio is reconnecting; try again.');
  await capture(page, 'second-factor-reconnecting');

  // A wrong code is refused by the server itself.
  await gate.getByLabel('Authenticator code').fill(wrongCode(secret));
  await gate.getByRole('button', { name: 'Verify authenticator' }).click();
  await expect(alert).toHaveText('The authenticator code is invalid.');
  await capture(page, 'second-factor-wrong-code');

  // The correct code succeeds; the old error disappears as soon as the new
  // attempt starts, before the server has answered.
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/auth/totp/confirm', async route => { await held; await route.continue(); }, { times: 1 });
  await gate.getByLabel('Authenticator code').fill(totp(secret));
  await gate.getByRole('button', { name: 'Verify authenticator' }).click();
  await expect(gate.getByRole('button', { name: 'Verify authenticator' })).toBeDisabled();
  await expect(alert).toHaveCount(0);
  release();

  await expect(gate.getByRole('heading', { name: 'Store your recovery codes' })).toBeVisible();
  await expect(gate.getByTestId('factor-recovery-codes')).toHaveText(/^([0-9A-F]{20} ?){10}$/);
  await expect(alert).toHaveCount(0);
  await capture(page, 'recovery-codes');

  await gate.getByRole('button', { name: 'I stored the codes' }).click();
  await expect(gate).toHaveCount(0);
});
