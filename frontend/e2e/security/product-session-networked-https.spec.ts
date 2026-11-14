import { createHmac } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect, request as playwrightRequest, type APIRequestContext, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3668: a Product (contributor) user bound to one project signs in through
 * the real sign-in form on the networked-https profile and files a card from
 * the Add task dialog. Nothing about auth, the registry or the watch paths is
 * mocked: the stack serves two projects and the session must see one.
 */
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'product-session');
const granted = 'Agent Studio Worktree';
const hidden = 'Restricted Fixture';
const noProjectSentence = 'You have no project yet; ask a workspace owner to add you to one.';

// One fresh stack per run: a retry would meet an Owner that already exists.
test.describe.configure({ mode: 'serial', retries: 0 });

let owner: APIRequestContext;

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

const sameOrigin = { 'Sec-Fetch-Site': 'same-origin' };

/** Invites a Product user on `projects` and sets the password the browser signs in with. */
async function inviteProduct(username: string, displayName: string, projects: string[], password: string) {
  const invite = await owner.post('/api/auth/invitations', { data: { username, displayName, role: 'contributor', projects } });
  expect(invite.ok(), `${invite.status()}: ${await invite.text()}`).toBeTruthy();
  const anonymous = await playwrightRequest.newContext({ baseURL: process.env.PW_BASE_URL, ignoreHTTPSErrors: true });
  const token = new URL((await invite.json()).signInLink as string, 'https://studio.invalid').searchParams.get('invite');
  expect(token).toBeTruthy();
  const accepted = await anonymous.post('/api/auth/invite/accept', { headers: sameOrigin,
    data: { token, newPassword: password } });
  expect(accepted.ok(), `${accepted.status()}: ${await accepted.text()}`).toBeTruthy();
  const user = (await accepted.json()).user as { id: string; role: string };
  await anonymous.dispose();
  expect(user.role).toBe('contributor');
  return user;
}

/** Every request the signed-in Product browser had refused. */
function recordRefusals(page: Page): string[] {
  const refused: string[] = [];
  page.on('response', response => {
    if (response.status() === 403) refused.push(`${response.request().method()} ${new URL(response.url()).pathname}`);
  });
  return refused;
}

async function signIn(page: Page, username: string, password: string): Promise<void> {
  await page.goto('/');
  await expect(page.getByTestId('auth-gate')).toBeVisible();
  await page.getByLabel('Username').fill(username);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page.getByTestId('auth-gate')).toHaveCount(0);
}

async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: join(resultDir, `${name}.png`) });
}

test.beforeAll(async () => {
  const baseURL = process.env.PW_BASE_URL!;
  const workspace = process.env.PW_E2E_WORKSPACE!;
  expect(baseURL.startsWith('https://')).toBeTruthy();
  mkdirSync(resultDir, { recursive: true });
  const anonymous = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true });
  const password = ['owner', 'pass', 'phrase', 'long', 'enough'].join('-');
  const bootstrap = await anonymous.post('/api/auth/bootstrap', { headers: sameOrigin,
    data: { username: 'studio-owner', displayName: 'Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const session = bootstrap.headers()['set-cookie']?.match(/__Host-agentstudio-session=([^;,\s]+)/)?.[1];
  const csrf = (await bootstrap.json()).csrfToken as string;
  await anonymous.dispose();
  expect(session && csrf).toBeTruthy();
  owner = await playwrightRequest.newContext({ baseURL, ignoreHTTPSErrors: true, timeout: 60_000, extraHTTPHeaders: {
    Cookie: `__Host-agentstudio-session=${session}`, 'X-CSRF-Token': csrf, ...sameOrigin } });
  const factor = await owner.post('/api/auth/totp/start');
  expect(factor.ok(), await factor.text()).toBeTruthy();
  const confirm = await owner.post('/api/auth/totp/confirm', { data: { code: totp((await factor.json()).secret) } });
  expect(confirm.ok(), await confirm.text()).toBeTruthy();
  // Both projects exist for the Owner; only the first is shared below.
  const ownerPaths = await owner.get('/api/watch-paths');
  expect((await ownerPaths.json() as { name: string }[]).map(entry => entry.name).sort()).toEqual([granted, hidden].sort());
});

test.afterAll(async () => { await owner?.dispose(); });

test('a Product user sees only the bound project and files a card from the Add task dialog', async ({ page }) => {
  test.setTimeout(240_000);
  const password = ['product', 'pass', 'phrase', 'long', 'enough'].join('-');
  await inviteProduct('carla.product', 'Carla Product', [granted], password);
  const title = `Product wish ${Date.now()}`;

  await page.setViewportSize({ width: 1440, height: 900 });
  const refused = recordRefusals(page);
  await signIn(page, 'carla.product', password);

  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);

    // Explorer: the bound project, nothing else.
    const row = page.getByTestId(`studio-explorer-project-row-${granted}`);
    await expect(row).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId(`studio-explorer-project-row-${hidden}`)).toHaveCount(0);
    await expect(page.getByTestId('studio-welcome-no-projects')).toHaveCount(0);
    await expect(page.locator('body')).not.toContainText(hidden);

    // Project picker: the bound project only.
    await page.getByTestId('studio-project-picker-trigger').click();
    await expect(page.getByTestId(`studio-project-picker-item-${granted}`)).toBeVisible();
    await expect(page.getByTestId(`studio-project-picker-item-${hidden}`)).toHaveCount(0);
    await shot(page, `product-project-picker-${theme}`);
    await page.getByTestId(`studio-project-picker-item-${granted}`).click();

    // Project Chat: the bound project is watched.
    const sheet = page.getByTestId('orch-side-sheet');
    // The open state survives the reload between themes.
    if (!(await sheet.isVisible()) || await sheet.evaluate(el => el.getBoundingClientRect().width < 300))
      await page.getByTestId('studio-titlebar-chat').click();
    await expect.poll(() => sheet.evaluate(el => el.getBoundingClientRect().width)).toBeGreaterThan(300);
    await expect(page.getByTestId('orch-side-sheet-no-project')).toHaveCount(0);
    await expect(sheet).toContainText(granted);
    await expect(sheet).not.toContainText(hidden);
    await shot(page, `product-project-chat-${theme}`);
    await page.reload();
  }

  // Add task dialog: the bound project is the only option and the default.
  await setTheme(page, 'light');
  await expect(page.getByTestId(`studio-explorer-project-row-${granted}`)).toBeVisible();
  await page.getByTestId('studio-board-add-task').click();
  await expect(page.getByTestId('create-title')).toBeVisible();
  // The Product role files into Backlog or Ready; the server refuses other lanes.
  await expect(page.getByTestId('create-lane-0-backlog')).toBeVisible();
  await expect(page.getByTestId('create-lane-2-ready')).toBeVisible();
  await expect(page.getByTestId('create-lane-1-preparation')).toHaveCount(0);
  const select = page.getByTestId('create-project-select');
  await expect(select.locator('option')).toHaveText([granted]);
  await page.getByTestId('create-title').fill(title);
  await page.getByTestId('create-prompt').fill('Make the board header calmer.');
  await shot(page, 'product-add-task-light');
  await setTheme(page, 'dark');
  await shot(page, 'product-add-task-dark');
  await page.setViewportSize({ width: 390, height: 844 });
  await shot(page, 'product-add-task-390');

  const created = page.waitForResponse(response => response.request().method() === 'POST'
    && /\/api\/(v1\/projects\/[^/]+\/)?tasks$/.test(new URL(response.url()).pathname));
  await page.getByTestId('create-submit').click();
  const response = await created;
  expect(response.status(), await response.text()).toBeLessThan(300);
  await expect(page.getByTestId('create-title')).toHaveCount(0);

  // The card belongs to the chosen project, read back through the Owner.
  const id = (await response.json() as { id: string }).id;
  expect(id).toBeTruthy();
  await expect.poll(async () => {
    const card = await owner.get(`/api/tasks/${encodeURIComponent(id)}`);
    return card.ok() ? (await card.json() as { info?: { title?: string; projectName?: string } }).info ?? null : null;
  }, { timeout: 30_000 }).toMatchObject({ title, projectName: granted, state: '0-backlog' });

  await shot(page, 'product-board-390');
  await page.setViewportSize({ width: 1440, height: 900 });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.getByText(title).first()).toBeVisible({ timeout: 30_000 });
    await shot(page, `product-board-${theme}`);
  }
  // No refusal reaches the user, and none of the routes this flow needs is refused.
  await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
  writeFileSync(join(resultDir, 'product-refused-requests.json'), JSON.stringify([...new Set(refused)].sort(), null, 2) + '\n');
  const flow = /^(GET \/api\/(v1\/)?(workspaces|projects)|GET \/api\/watch-paths|GET \/api\/v1\/studio\/(board|runner\/status)|POST \/api\/tasks|GET \/api\/runner\/[^/]+\/orchestrator-chat)$/;
  expect(refused.filter(request => flow.test(request))).toEqual([]);
});

test('a Product user without a project reads one sentence instead of an empty picker', async ({ page }) => {
  test.setTimeout(120_000);
  const password = ['projectless', 'pass', 'phrase', 'long'].join('-');
  const user = await inviteProduct('dana.product', 'Dana Product', [granted], password);
  // The Owner removes the only project binding.
  const iam = await owner.get('/api/auth/iam');
  expect(iam.ok(), await iam.text()).toBeTruthy();
  const bindings = (await iam.json()).bindings as { id: string; principalId: string }[];
  const own = bindings.filter(binding => binding.principalId === user.id);
  expect(own.length).toBeGreaterThan(0);
  for (const binding of own) {
    const removed = await owner.delete(`/api/auth/iam/bindings/${encodeURIComponent(binding.id)}`);
    expect(removed.ok(), `${removed.status()}: ${await removed.text()}`).toBeTruthy();
  }

  await page.setViewportSize({ width: 1440, height: 900 });
  await signIn(page, 'dana.product', password);
  const sentence = page.getByTestId('studio-welcome-no-projects');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(sentence).toHaveText(noProjectSentence);
    // One sentence: no empty Explorer tree, picker or Add task beside it.
    await expect(page.getByText(noProjectSentence)).toHaveCount(1);
    await expect(page.getByTestId('studio-board-add-task')).toHaveCount(0);
    await expect(page.getByTestId('error-dialog-overlay')).toHaveCount(0);
    await expect(page.getByTestId('studio-project-picker-trigger')).toHaveCount(0);
    await expect(page.locator('body')).not.toContainText(hidden);
    await shot(page, `product-no-project-${theme}`);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  // The narrow layout folds the picker into More navigation; it offers no Switch project.
  // Sign-in can leave an informational toast over the narrow navigation button.
  while (await page.getByTestId('notification-info').count())
    await page.getByTestId('notification-info').first().getByTestId('notification-close').click();
  await page.getByRole('button', { name: 'More navigation' }).click();
  await expect(page.getByRole('menuitem', { name: 'Project chat' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Switch project' })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(sentence).toBeVisible();
  await expect(sentence).toHaveText(noProjectSentence);
  await shot(page, 'product-no-project-390');
});
