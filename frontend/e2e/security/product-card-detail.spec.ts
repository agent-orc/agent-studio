import { createHmac } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect, request as playwrightRequest, type APIRequestContext, type Page, type Request } from '@playwright/test';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3669: a Product session opens cards on the networked profile behind
 * the HTTPS-forwarding frontend hop, seeded with the demo workspace
 * (`networked-https-demo`). The card shows its title, prompt, delivery and
 * review summary; no detail request is answered 403, and the refusal
 * message, when one shows, names access.
 */
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'product-card-detail');

function base32(secret: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const char of secret.replace(/=+$/, '').toUpperCase()) bits += alphabet.indexOf(char).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let index = 0; index + 8 <= bits.length; index += 8) bytes.push(parseInt(bits.slice(index, index + 8), 2));
  return Buffer.from(bytes);
}

/** RFC 6238 code, the same algorithm the authenticator app uses. */
function totp(secret: string, at = Date.now()): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 30_000)));
  const digest = createHmac('sha1', base32(secret)).update(counter).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

const sessionCookie = (header: string | undefined) => header?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];

/** Bootstrap the Owner with a second factor and invite one Product user into the demo project. */
async function inviteBoardUsers(baseURL: string, workspace: string): Promise<{ product: string; approver: string }> {
  const anonymous = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true,
    extraHTTPHeaders: { 'Sec-Fetch-Site': 'same-origin' } });
  const password = ['owner', 'pass', 'phrase', 'long', 'enough'].join('-');
  const bootstrap = await anonymous.post('/api/auth/bootstrap', { data: {
    username: 'studio-owner', displayName: 'Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const owner: APIRequestContext = await playwrightRequest.newContext({ baseURL, timeout: 60_000, ignoreHTTPSErrors: true, extraHTTPHeaders: {
    Cookie: `__Host-agentstudio-session=${sessionCookie(bootstrap.headers()['set-cookie'])}`,
    'X-CSRF-Token': (await bootstrap.json()).csrfToken as string, 'Sec-Fetch-Site': 'same-origin' } });
  const factor = await owner.post('/api/auth/totp/start');
  expect(factor.ok(), await factor.text()).toBeTruthy();
  const confirm = await owner.post('/api/auth/totp/confirm', { data: { code: totp((await factor.json()).secret) } });
  expect(confirm.ok(), await confirm.text()).toBeTruthy();

  const workspaces = await owner.get('/api/v1/workspaces');
  expect(workspaces.ok(), await workspaces.text()).toBeTruthy();
  const projects = (await workspaces.json() as { projects: { id: string; displayName: string }[] }[])
    .flatMap(item => item.projects);
  const demo = projects.find(project => project.displayName === 'Demo App');
  expect(demo, JSON.stringify(projects)).toBeTruthy();
  const invite = await owner.post('/api/auth/invitations', { data: {
    username: 'carla', displayName: 'Carla', role: 'contributor', projects: [demo!.id] } });
  expect(invite.ok(), await invite.text()).toBeTruthy();
  const product = (await invite.json()).signInLink as string;
  const approverInvite = await owner.post('/api/auth/invitations', { data: {
    username: 'alex', displayName: 'Alex', role: 'viewer', projects: [demo!.id],
    bindings: [{ role: 'approver', scope: { kind: 'project', project: demo!.id } }],
  } });
  expect(approverInvite.ok(), await approverInvite.text()).toBeTruthy();
  const approver = (await approverInvite.json()).signInLink as string;
  await owner.dispose();
  await anonymous.dispose();
  return { product, approver };
}

/** Every API request the browser saw answered 403 after the Product user signed in. */
const refusals: string[] = [];

function watchRefusals(page: Page, phase: () => string): void {
  const requests = new WeakMap<Request, string>();
  page.on('request', request => requests.set(request, phase()));
  page.on('response', response => {
    const url = new URL(response.url());
    if (response.status() === 403 && url.pathname.startsWith('/api/'))
      refusals.push(`${requests.get(response.request()) ?? phase()} ${response.request().method()} ${url.pathname}`);
  });
}

async function openCard(page: Page, key: string, title: string): Promise<void> {
  await page.goto(`/#/tasks/${key}`);
  await dismissDevErrorDialog(page);
  const detail = page.getByTestId('studio-task');
  await expect(detail.getByTestId('overview-title')).toContainText(title, { timeout: 30_000 });
  await expect(page.getByTestId('task-detail-load-error')).toHaveCount(0);
}

async function expectDeliveryAndReview(page: Page): Promise<void> {
  const detail = page.getByTestId('studio-task');
  await expect(detail.getByTestId('delivery-claim-panel')).toBeVisible();
  await expect(detail.getByTestId('result-summary-meta')).toContainText('Human review');
}

/** The prompt lives on the inspector's Task tab. */
async function showPrompt(page: Page): Promise<void> {
  await page.getByTestId('inspector-tab-task').click();
  await expect(page.getByTestId('studio-task').getByText('Pinned demo data').first()).toBeVisible();
}

// One fresh stack per run: a retry would meet an Owner that already exists.
test.describe.configure({ mode: 'serial', retries: 0 });

test.afterEach(() => {
  mkdirSync(resultDir, { recursive: true });
  writeFileSync(join(resultDir, 'refused-reads.txt'), [...new Set(refusals)].join('\n') + '\n');
});

test('Product and Approver load the board without refused reads; Product card details remain available', async ({ page, browser }) => {
  test.setTimeout(360_000);
  const baseURL = process.env.PW_BASE_URL!;
  const workspace = process.env.PW_E2E_WORKSPACE!;
  expect(baseURL && workspace).toBeTruthy();
  mkdirSync(resultDir, { recursive: true });
  const signInLinks = await inviteBoardUsers(baseURL, workspace);

  // The Product user joins through the invitation page; the HTTPS hop lets
  // the browser keep the Secure session cookie like a deployed Studio.
  let phase = 'invitation';
  watchRefusals(page, () => phase);
  await page.goto(signInLinks.product);
  await page.getByLabel('New password').fill(['product', 'pass', 'phrase', 'long', 'enough'].join('-'));
  await page.getByRole('button', { name: 'Join project' }).click();
  await expect(page.getByTestId('auth-gate')).toHaveCount(0);
  const status = await page.request.get('/api/auth/status');
  expect(await status.json()).toMatchObject({ profile: 'networked', authenticated: true, user: { role: 'contributor' } });

  phase = 'board';
  for (const theme of ['light', 'dark'] as const) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await setTheme(page, theme);
    await expect(page.getByTestId('studio-sidebar')).toBeVisible();
    await expect(page.getByTestId('studio-board')).toBeVisible();
    await expect(page.getByTestId('status-bar-limited')).toBeVisible();
    await expect(page.getByTestId('status-bar-running')).toHaveCount(0);
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    await page.waitForTimeout(1_500);
    expect(refusals, `${theme} Product board responses answered 403`).toEqual([]);
    await page.screenshot({ path: join(resultDir, `product-board-${theme}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByTestId('studio-board')).toBeVisible();
  await expect(page.getByTestId('status-bar-limited')).toBeVisible();
  await page.waitForTimeout(1_500);
  expect(refusals, '390 px Product board responses answered 403').toEqual([]);
  await page.screenshot({ path: join(resultDir, 'product-board-390.png'), fullPage: true });

  const cards = [
    { key: 'DEMO-3', title: 'Update the accessibility test baseline', lane: 'ready', review: false },
    { key: 'DEMO-5', title: 'Add CSV export to reports', lane: 'human-review', review: true },
  ];
  for (const theme of ['light', 'dark'] as const) {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');
    await setTheme(page, theme);
    for (const card of cards) {
      await openCard(page, card.key, card.title);
      const detail = page.getByTestId('studio-task');
      // A card in review shows its delivery claim and, on the Result tab, the review summary.
      if (card.review) await expectDeliveryAndReview(page);
      await expect(detail.getByTestId('task-resource-status').filter({ hasText: 'Usage' })).toHaveCount(0);
      // The live hub keeps the network busy; let the enrichment reads settle.
      await page.waitForTimeout(1_500);
      await page.screenshot({ path: join(resultDir, `${card.lane}-${card.key}-${theme}.png`) });
      await showPrompt(page);
      await page.screenshot({ path: join(resultDir, `${card.lane}-${card.key}-${theme}-prompt.png`) });
    }
  }

  await page.setViewportSize({ width: 390, height: 844 });
  for (const card of cards) {
    await openCard(page, card.key, card.title);
    if (card.review) await expectDeliveryAndReview(page);
    // The live hub keeps the network busy; let the enrichment reads settle.
    await page.waitForTimeout(1_500);
    await page.screenshot({ path: join(resultDir, `${card.lane}-${card.key}-390.png`), fullPage: true });
  }

  // The card-open contract: no task read is refused. Shell reads outside
  // /api/tasks are recorded in refused-reads.txt for the Product shell.
  expect(refusals, 'Product shell and card reads answered 403').toEqual([]);

  const approverPage = await browser.newPage({ ignoreHTTPSErrors: true });
  phase = 'approver invitation';
  watchRefusals(approverPage, () => phase);
  await approverPage.goto(signInLinks.approver);
  await approverPage.getByLabel('New password').fill(['approver', 'pass', 'phrase', 'long', 'enough'].join('-'));
  await approverPage.getByRole('button', { name: 'Join project' }).click();
  await expect(approverPage.getByTestId('auth-gate')).toHaveCount(0);
  phase = 'approver board';
  await approverPage.goto('/');
  await expect(approverPage.getByTestId('studio-sidebar')).toBeVisible();
  await expect(approverPage.getByTestId('studio-board')).toBeVisible();
  await expect(approverPage.getByTestId('status-bar-limited')).toBeVisible();
  await approverPage.waitForTimeout(1_500);
  expect(refusals, 'Approver board responses answered 403').toEqual([]);
  await approverPage.close();
});
