import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { basename, join } from 'node:path';
import { setTheme } from '../helpers/theme';

const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'owner-bootstrap');

test('anonymous startup 401 keeps owner enrollment available over HTTPS at desktop and phone widths', async ({ page }) => {
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace, 'run through npm run e2e with the isolated HTTPS profile').toBeTruthy();
  mkdirSync(resultDir, { recursive: true });
  await page.setViewportSize({ width: 1280, height: 960 });

  let releaseEnvironment!: () => void;
  const environmentHeld = new Promise<void>(resolve => { releaseEnvironment = resolve; });
  await page.route('**/api/environment', async route => {
    await environmentHeld;
    await route.fulfill({ status: 401, contentType: 'application/json',
      body: JSON.stringify({ error: 'authentication-required' }) });
  });

  await page.goto('/');
  expect(new URL(page.url()).protocol).toBe('https:');
  const gate = page.getByTestId('auth-gate');
  const ownerHeading = gate.getByRole('heading', { name: 'Create the first owner' });
  await expect(ownerHeading).toBeVisible();
  const environment401 = page.waitForResponse(response =>
    new URL(response.url()).pathname === '/api/environment' && response.status() === 401);
  releaseEnvironment();
  await environment401;
  await expect(ownerHeading).toBeVisible();
  await setTheme(page, 'light');
  await page.screenshot({ path: join(resultDir, 'owner-bootstrap-1280-light.png'), fullPage: true });

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(ownerHeading).toBeVisible();
  await expect(gate.getByRole('button', { name: 'Create owner' })).toBeVisible();
  await page.screenshot({ path: join(resultDir, 'owner-bootstrap-390-light.png'), fullPage: true });

  await gate.getByLabel('Username').fill('owner');
  await gate.getByLabel('Display name').fill('Owner');
  await gate.getByLabel('Installer owner code').fill(`${basename(workspace!)}-owner-code`);
  await gate.getByLabel('Password').fill('studio-setup-passphrase');
  await gate.getByRole('button', { name: 'Create owner' }).click();
  await expect(gate.getByRole('heading', { name: 'Set up a second factor' })).toBeVisible();
});
