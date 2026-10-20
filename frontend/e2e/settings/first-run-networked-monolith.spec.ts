import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from '../fixtures/dev-backend';

const resultDir = process.env.JOB_RESULTS_DIR ?? 'test-results';

test('networked monolith explains that the first-run Task Server plane is unavailable', async ({ page, devBackend }) => {
  expect(process.env.PW_E2E_PROFILE).toBe('networked-monolith');
  const bootstrap = await page.request.post(`${devBackend.baseUrl}/api/auth/bootstrap`, {
    headers: { 'X-Forwarded-Proto': 'https', 'Sec-Fetch-Site': 'same-origin' },
    data: { username: 'first.owner', displayName: 'Owner',
      password: 'correct horse battery staple!', bootstrapCode: 'e2e-owner-code' },
  });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const sessionCookie = bootstrap.headers()['set-cookie']?.split(';', 1)[0];
  expect(sessionCookie).toBeTruthy();

  // Forward browser API calls through the test's loopback frontend proxy to the
  // real networked monolith. X-Forwarded-Proto represents its TLS ingress.
  await page.route('**/api/**', async route => {
    const response = await route.fetch({ headers: {
      ...route.request().headers(),
      'X-Forwarded-Proto': 'https',
      Cookie: sessionCookie!,
    } });
    await route.fulfill({ response });
  });

  const auth = await page.request.get(`${devBackend.baseUrl}/api/auth/status`, {
    headers: { 'X-Forwarded-Proto': 'https', Cookie: sessionCookie! },
  });
  expect(auth.ok()).toBeTruthy();
  expect((await auth.json() as { profile: string; authenticated: boolean }))
    .toMatchObject({ profile: 'networked', authenticated: true });
  const installation = await page.request.get(`${devBackend.baseUrl}/api/v1/installation`, {
    headers: { 'X-Forwarded-Proto': 'https', Cookie: sessionCookie! },
  });
  expect(installation.status()).toBe(404);

  await page.goto('/#/workspace/settings/first-run');
  const unavailable = page.getByTestId('first-run-task-server-unavailable');
  await expect(unavailable).toContainText('running without a Task Server connection');
  await expect(page.getByTestId('first-run-owner')).toHaveAttribute('data-state', 'pending');

  mkdirSync(resultDir, { recursive: true });
  await page.screenshot({ path: join(resultDir, 'first-run-networked-monolith.png'), fullPage: true });
});
