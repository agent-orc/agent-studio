import { createHash, createHmac } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3670: the Reviewer role on the networked-https profile, a networked
 * monolith behind a real TLS proxy, so the browser keeps its Secure session
 * cookies. The Owner invites a reviewer through the invite form; the reviewer
 * accepts the invitation, sends one Human Review card
 * back with a note, and accepts another. No API response is mocked.
 */
const resultDir = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'reviewer-role');
const projectName = 'Agent Studio Worktree';
const note = 'The header is still loud. Use the quiet tone from the style guide, then deliver again.';

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

interface Card { id: string; key: string; folderPath: string; state: string }

/** Studio writes some card files with a UTF-8 byte order mark. */
const readText = (path: string) => readFileSync(path, 'utf8').replace(/^\uFEFF/, '');
const readJsonLines = (path: string) => readText(path).trim().split('\n').map(line => JSON.parse(line.replace(/^\uFEFF/, '')));

async function shoot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: join(resultDir, `${name}.png`), fullPage: false });
}

/** Light and dark at desktop width, then both themes at 390 px; restores the desktop light view. */
async function shootThemes(page: Page, name: string, locator?: () => Promise<void>): Promise<void> {
  for (const [width, height] of [[1440, 900], [390, 844]] as const) {
    await page.setViewportSize({ width, height });
    for (const theme of ['light', 'dark'] as const) {
      await setTheme(page, theme);
      await locator?.();
      await shoot(page, `${name}-${width}-${theme}`);
    }
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await setTheme(page, 'light');
}

// One fresh stack per run: a retry would meet an Owner that already exists.
test.describe.configure({ mode: 'serial', retries: 0 });

test('a Reviewer is invited, sends a Human Review card back with a note, and accepts another', async ({ browser, page }) => {
  test.skip(process.env.PW_E2E_PROFILE !== 'networked-https', 'Run with the networked-https Playwright profile.');
  test.setTimeout(240_000);
  const baseURL = process.env.PW_BASE_URL!;
  const workspace = process.env.PW_E2E_WORKSPACE!;
  const watchPath = join(workspace, 'projects', 'agent-studio-worktree');
  expect(baseURL.startsWith('https://')).toBe(true);
  mkdirSync(resultDir, { recursive: true });
  await page.setViewportSize({ width: 1440, height: 900 });

  // --- Owner: bootstrap and second factor through the browser's own cookie jar.
  const password = ['owner', 'pass', 'phrase', 'long', 'enough'].join('-');
  const bootstrap = await page.request.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' },
    data: { username: 'studio-owner', displayName: 'Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  expect(bootstrap.ok(), `${bootstrap.status()}: ${await bootstrap.text()}`).toBeTruthy();
  const ownerHeaders = { 'X-CSRF-Token': (await bootstrap.json()).csrfToken as string, 'Sec-Fetch-Site': 'same-origin' };
  // Real TLS: the Secure, host-only session cookie is in the browser context.
  expect((await page.context().cookies()).map(cookie => cookie.name)).toContain('__Host-agentstudio-session');
  const factor = await page.request.post('/api/auth/totp/start', { headers: ownerHeaders });
  expect(factor.ok(), await factor.text()).toBeTruthy();
  const confirmed = await page.request.post('/api/auth/totp/confirm', { headers: ownerHeaders,
    data: { code: totp((await factor.json()).secret) } });
  expect(confirmed.ok(), await confirmed.text()).toBeTruthy();
  const owner: APIRequestContext = page.request;

  // A networked Studio runs cards on remote runners; the sent-back card waits for one.
  const execution = await owner.put(`/api/projects/${encodeURIComponent(projectName)}/execution-runner`, {
    headers: ownerHeaders, data: { executionLocation: 'class:linux', remoteExecutionEnabled: true } });
  expect(execution.ok(), await execution.text()).toBeTruthy();

  const ownerRead = async (id: string): Promise<Card> => {
    for (let attempt = 0; attempt < 60; attempt++) {
      const detail = await owner.get(`/api/tasks/${encodeURIComponent(id)}`, { params: { watchPath } });
      if (detail.status() === 200) return (await detail.json()).info as Card;
      expect(detail.status(), await detail.text()).toBe(202);
      await new Promise(settle => setTimeout(settle, 500));
    }
    throw new Error(`Card ${id} stayed unreadable`);
  };
  /** A delivered card with a passing content verdict for its brief, parked in Human Review. */
  const deliveredCard = async (title: string): Promise<Card> => {
    const created = await owner.post('/api/tasks', { headers: ownerHeaders, data: { id: '', title, watchPath,
      agent: 'claude', cliType: 'claude', targetState: '2-ready', requiresIntegration: false, fixture: false,
      promptMarkdown: `Reviewer role end-to-end fixture: ${title}.` } });
    expect(created.ok(), await created.text()).toBeTruthy();
    const card = await ownerRead((await created.json()).id);
    const briefFile = join(card.folderPath, '.metadata', 'brief-version');
    const brief = existsSync(briefFile) ? readFileSync(briefFile, 'utf8').trim()
      : createHash('sha256').update(readFileSync(join(card.folderPath, 'prompt.md'), 'utf8')).digest('hex');
    writeFileSync(join(card.folderPath, 'content-review-context.json'), JSON.stringify({ briefVersion: brief }));
    writeFileSync(join(card.folderPath, 'aspect-requirement-fit.json'), JSON.stringify({ status: 'pass',
      summary: 'The delivery matches the brief.', evidenceChecked: 'Reviewer role end-to-end fixture.' }));
    writeFileSync(join(card.folderPath, 'status.md'), `# ${title}\n\nThe delivery is ready for review.\n`);
    mkdirSync(join(card.folderPath, 'results'), { recursive: true });
    writeFileSync(join(card.folderPath, 'results', 'deliverables.md'), `# Deliverables\n\n${title}.\n`);
    const moved = await owner.post(`/api/tasks/${encodeURIComponent(card.id)}/move`, { headers: ownerHeaders,
      params: { watchPath }, data: { targetState: '5-human-review' } });
    expect(moved.ok(), await moved.text()).toBeTruthy();
    return ownerRead(card.id);
  };
  const sendBackCard = await deliveredCard('Calm the board header');
  const acceptCard = await deliveredCard('Quiet the board footer');
  expect([sendBackCard.state, acceptCard.state]).toEqual(['5-human-review', '5-human-review']);

  // --- Owner invites the reviewer through the invite form.
  let inviteLink = '';
  await test.step('Owner invites a Reviewer and reads who the role is for', async () => {
    await page.goto('/#/workspace/settings/access', { waitUntil: 'domcontentloaded', timeout: 30_000 });
    const access = page.getByTestId('access-area');
    await expect(access.getByRole('heading', { name: 'Users' })).toBeVisible({ timeout: 30_000 });
    const invite = access.getByTestId('access-invite-form');
    await invite.getByTestId('access-invite-username').fill('rui.reviewer');
    await invite.getByTestId('access-invite-display-name').fill('Rui Reviewer');
    await invite.getByTestId('access-grant-project').selectOption({ label: projectName });
    await invite.getByTestId('access-grant-role').selectOption('reviewer');
    await expect(invite.getByTestId('access-grant-role-description'))
      .toContainText('Reviews and accepts cards in Human Review.');
    await shootThemes(page, 'owner-invite-reviewer', () => invite.scrollIntoViewIfNeeded());
    await invite.getByTestId('access-invite-submit').click();
    await expect(invite.getByTestId('access-invite-link')).toContainText('/?invite=');
    inviteLink = (await invite.getByTestId('access-invite-link').textContent())!.trim();
    await expect(access.getByTestId('access-user-rui.reviewer')).toContainText('Invited');
  });

  // --- The reviewer: own browser context, invitation, second factor.
  const reviewerContext = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
  const reviewer = await reviewerContext.newPage();
  await reviewer.setViewportSize({ width: 1440, height: 900 });
  await test.step('Reviewer accepts the invitation and enters the Studio', async () => {
    await reviewer.goto(inviteLink.slice(inviteLink.indexOf('/?invite=')));
    await expect(reviewer.getByRole('heading', { name: 'Accept invitation' })).toBeVisible({ timeout: 30_000 });
    await reviewer.getByLabel('New password').fill(['reviewer', 'pass', 'phrase', 'long'].join('-'));
    await reviewer.getByRole('button', { name: 'Join project' }).click();
    // The installation's second-factor policy decides whether the gate asks
    // for enrollment; follow it when it does.
    const enroll = reviewer.getByRole('heading', { name: 'Set up a second factor' });
    const gate = reviewer.getByTestId('auth-gate');
    await expect.poll(async () => await enroll.isVisible() || await gate.count() === 0, { timeout: 30_000 }).toBe(true);
    if (await enroll.isVisible()) {
      await reviewer.getByRole('button', { name: 'Set up authenticator' }).click();
      const secret = (await reviewer.getByTestId('totp-secret').textContent())!.trim();
      await reviewer.getByLabel('Authenticator code').fill(totp(secret));
      await reviewer.getByRole('button', { name: 'Verify authenticator' }).click();
      await reviewer.getByRole('button', { name: 'I stored the codes' }).click();
    }
    await expect(gate).toHaveCount(0, { timeout: 30_000 });
    const status = await (await reviewer.request.get('/api/auth/status')).json();
    expect(status.user).toMatchObject({ displayName: 'Rui Reviewer', role: 'viewer', reviewerProjects: [projectName] });
  });

  const openCard = async (card: Card) => {
    await reviewer.goto(`/?job=${encodeURIComponent(card.id)}&watchPath=${encodeURIComponent(watchPath)}`);
    await expect(reviewer.getByTestId('pane-prompt')).toBeVisible({ timeout: 30_000 });
  };

  await test.step('Reviewer reads the card and sees the review decision, not lane controls', async () => {
    await openCard(sendBackCard);
    const panel = reviewer.getByTestId('human-review-decision');
    await expect(panel).toBeVisible({ timeout: 30_000 });
    await expect(panel).toContainText('Your review');
    await expect(reviewer.getByTestId('triage-action-mark-done')).toHaveCount(0);
    await expect(reviewer.getByTestId('continue-round-panel')).toHaveCount(0);
    await expect(reviewer.getByText('Checking git status…')).toHaveCount(0);
    await shootThemes(reviewer, 'reviewer-human-review-card', () => panel.scrollIntoViewIfNeeded());
  });

  await test.step('Reviewer sends the card back with a note for the next run', async () => {
    const panel = reviewer.getByTestId('human-review-decision');
    await panel.getByTestId('human-review-send-back-open').click();
    await panel.getByTestId('human-review-note').fill(note);
    await shootThemes(reviewer, 'reviewer-send-back-note', () => panel.scrollIntoViewIfNeeded());
    await panel.getByTestId('human-review-send-back').click();
    await expect(reviewer.getByText('Sent back. The card moved to Ready with your note for the next run.')).toBeVisible();

    const ready = await ownerRead(sendBackCard.id);
    expect(ready.state).toBe('2-ready');
    const intent = JSON.parse(readText(join(ready.folderPath, 'pending-intent.json')));
    expect(intent.prompt).toContain(note);
    expect(intent.prompt).toContain('Rui Reviewer reviewed this delivery in Human Review');
    expect(readText(join(ready.folderPath, 'prompt.md'))).toContain(note);
    const timeline = readJsonLines(join(ready.folderPath, 'logs', 'timeline.jsonl'));
    expect(timeline).toContainEqual(expect.objectContaining({ kind: 'human_review_decided', actor: 'human:Rui Reviewer',
      details: expect.objectContaining({ decision: 'send-back', note }) }));
  });

  await test.step('The send back is on the card timeline', async () => {
    await openCard(sendBackCard);
    await reviewer.getByTestId('prompt-tab-timeline').click();
    const event = reviewer.getByTestId('timeline-event').filter({ hasText: 'Human review sent back' });
    await expect(event).toBeVisible({ timeout: 15_000 });
    await expect(event).toContainText(note);
    await shootThemes(reviewer, 'reviewer-send-back-timeline', () => event.scrollIntoViewIfNeeded());
  });

  await test.step('Reviewer accepts the other card', async () => {
    await openCard(acceptCard);
    const panel = reviewer.getByTestId('human-review-decision');
    await expect(panel).toBeVisible({ timeout: 30_000 });
    await panel.getByTestId('human-review-accept').click();
    await expect(reviewer.getByText('Accepted. The card moved to Accepted.')).toBeVisible();
    await expect.poll(async () => (await ownerRead(acceptCard.id)).state, { timeout: 15_000 }).toBe('6-completed');
    await shoot(reviewer, 'reviewer-accepted-1440-light');
  });

  await test.step('Both decisions are audited with the reviewer as actor', async () => {
    const users = await (await owner.get('/api/auth/users')).json() as { id: string; username: string }[];
    const rui = users.find(user => user.username === 'rui.reviewer')!;
    const audit = readJsonLines(join(workspace, '.security', 'control-plane-audit.jsonl'))
      .filter(entry => entry.stream === 'review');
    expect(audit.map(entry => [entry.operation, entry.actor])).toEqual([
      ['card.review.send-back', `human:${rui.id}`],
      ['card.review.accept', `human:${rui.id}`],
    ]);
    writeFileSync(join(resultDir, 'review-audit.json'), JSON.stringify(audit, null, 2));
  });

  await reviewerContext.close();
});
