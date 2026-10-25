import { chromium, expect } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';

const url = process.env.STUDIO_SMOKE_URL;
const username = process.env.STUDIO_SMOKE_USERNAME;
const password = process.env.STUDIO_SMOKE_PASSWORD;
if (!url || !username || !password) {
  throw new Error('The Compose browser smoke requires its URL and owner credentials.');
}

// The private Caddy fixture uses an internal certificate and a local test name.
const browser = await chromium.launch({
  args: ['--host-resolver-rules=MAP studio.test 127.0.0.1'],
});
try {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  const apiResponses = [];
  page.on('response', response => {
    const pathname = new URL(response.url()).pathname;
    if (pathname.startsWith('/api/')) {
      apiResponses.push({ method: response.request().method(), pathname, status: response.status() });
    }
  });
  await page.goto(url, { waitUntil: 'domcontentloaded' });
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible({ timeout: 30_000 });

  // The disconnected live-update hub marks the backend offline after four seconds.
  await page.waitForTimeout(4_600);
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password').fill(password);
  const login = page.waitForResponse(response =>
    response.url() === `${url}/api/auth/login`
    && response.request().method() === 'POST', { timeout: 30_000 });
  await page.getByRole('button', { name: 'Sign in' }).click();
  const loginResponse = await login;
  if (!loginResponse.ok()) throw new Error(`Browser sign-in returned ${loginResponse.status()}`);
  await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 30_000 });
  const boardResponse = apiResponses.find(response =>
    response.method === 'GET'
    && response.status === 200
    && (response.pathname === '/api/tasks/grouped'
      || response.pathname === '/api/v1/studio/board'));
  if (!boardResponse) {
    throw new Error(`The browser opened no live board response: ${JSON.stringify(apiResponses)}`);
  }

  if (process.env.JOB_RESULTS_DIR) {
    await mkdir(process.env.JOB_RESULTS_DIR, { recursive: true });
    await page.screenshot({ path: join(process.env.JOB_RESULTS_DIR, 'website-board.png') });
  }
  await context.close();
} finally {
  await browser.close();
}
