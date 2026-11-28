import { test, expect, request, type APIRequestContext } from '@playwright/test';
import { createHmac, generateKeyPairSync, createHash, sign, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { setTheme } from '../helpers/theme';

function totp(secret: string): string {
  let bits = '';
  for (const char of secret.replace(/=+$/, '')) bits += 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(char).toString(2).padStart(5, '0');
  const bytes: number[] = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const digest = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  return String((digest.readUInt32BE(digest[digest.length - 1] & 15) & 0x7fffffff) % 1000000).padStart(6, '0');
}
async function ok(response: Awaited<ReturnType<APIRequestContext['post']>>) {
  expect(response.ok(), `${response.status()}: ${await response.text()}`).toBeTruthy();
  const body = await response.text();
  return body ? JSON.parse(body) : null;
}

test.describe.configure({ retries: 0 });
test('nine integrated override acceptances use one step-up at 390 px', async ({ page }) => {
  test.setTimeout(600000);
  const browserErrors: string[] = [];
  const failedRequests: { path: string; error: string | null }[] = [];
  const viewportChecks: { width: number; theme: string; documentOverflow: boolean; sheetOverflow: boolean }[] = [];
  page.on('requestfailed', req => failedRequests.push({ path: new URL(req.url()).pathname, error: req.failure()?.errorText ?? null }));
  page.on('pageerror', error => browserErrors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') browserErrors.push(message.text()); });
  const workspace = process.env.PW_E2E_WORKSPACE!;
  const baseURL = process.env.PW_BASE_URL!;
  expect(process.env.PW_E2E_TLS_CERT).toBeTruthy();
  const results = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'operator-batch');
  mkdirSync(results, { recursive: true });
  const password = ['ocean', 'birch', 'trail', '2026'].join('-');
  const bootstrapClient = await request.newContext({ baseURL, ignoreHTTPSErrors: true, extraHTTPHeaders: { 'X-Client-Id': 'local-default' } });
  const bootstrapResponse = await bootstrapClient.post('/api/auth/bootstrap', { headers: { 'Sec-Fetch-Site': 'same-origin' },
    data: { username: 'batch-owner', displayName: 'Batch Owner', password, bootstrapCode: `${basename(workspace)}-owner-code` } });
  const bootstrap = await ok(bootstrapResponse);
  const session = bootstrapResponse.headers()['set-cookie'].match(/__Host-agentstudio-session=([^;,\s]+)/)![1];
  await bootstrapClient.dispose();
  const anonymous = await request.newContext({ baseURL, ignoreHTTPSErrors: true, extraHTTPHeaders: { 'X-Client-Id': 'local-default' } });
  const owner = await request.newContext({ baseURL, ignoreHTTPSErrors: true, extraHTTPHeaders: {
    'X-Client-Id': 'local-default', Cookie: `__Host-agentstudio-session=${session}`, 'X-CSRF-Token': bootstrap.csrfToken, 'Sec-Fetch-Site': 'same-origin' } });
  const enrollment = await ok(await owner.post('/api/auth/totp/start'));
  const factors = await ok(await owner.post('/api/auth/totp/confirm', { data: { code: totp(enrollment.secret) } }));
  let recovery = 0;
  const stepUp = async () => ok(await owner.post('/api/auth/step-up', { data: { code: factors.recoveryCodes[recovery++] } }));
  const agent = await ok(await owner.post('/api/auth/iam/operator-agents', { data: { name: 'ops console',
    bindings: [{ role: 'operator-agent', scope: { kind: 'workspace' } }] } }));
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const jwk = publicKey.export({ format: 'jwk' });
  const proofFor = (token: string, path: string, method = 'POST') => {
    const data = { htm: method, htu: `${baseURL}${path}`, iat: Math.floor(Date.now() / 1000), jti: randomUUID(),
      ath: createHash('sha256').update(token).digest('base64url') };
    const unsigned = [Buffer.from(JSON.stringify({ typ: 'dpop+jwt', alg: 'ES256', jwk })).toString('base64url'),
      Buffer.from(JSON.stringify(data)).toString('base64url')].join('.');
    return `${unsigned}.${sign('sha256', Buffer.from(unsigned), { key: privateKey, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
  };
  const start = await ok(await anonymous.post('/api/auth/operator-sign-in', { data: {
    publicKey: jwk, deviceLabel: 'Batch test console', operatorAgent: agent.id, classes: ['cards.accept-override'] } }));
  await stepUp();
  await ok(await owner.post('/api/auth/iam/operator-sign-ins/confirm', { data: {
    code: start.userCode, operatorAgentId: agent.id, classes: ['cards.accept-override'], lifetimeHours: 8 } }));
  const issued = await ok(await anonymous.post('/api/auth/operator-sign-in/poll', { headers: { DPoP: proofFor(start.pollHandle, '/api/auth/operator-sign-in/poll') }, data: { pollHandle: start.pollHandle } }));
  const paths = await (await owner.get('/api/watch-paths')).json();
  const wp = paths[0];
  const checkout = process.env.PW_E2E_CHECKOUT!;
  const sha = execFileSync('git', ['-C', checkout, 'log', '--no-merges', '-1', '--format=%H', 'refs/remotes/origin/HEAD', '--', 'README.md'], { encoding: 'utf8' }).trim();
  const files = execFileSync('git', ['-C', checkout, 'diff-tree', '--root', '--no-commit-id', '--name-only', '-r', sha], { encoding: 'utf8' }).trim().split('\n').filter(Boolean);
  expect(files.length).toBeGreaterThan(0);
  // Attribute a known published commit without changing any shared Git ref.
  const items = [];
  for (let i = 1; i <= 9; i++) {
    // A code-free review fixture gains its real code-delivery evidence below.
    const created = await ok(await owner.post('/api/tasks', { data: { id: `batch-accept-${i}`, title: `Reviewed delivery ${i}`,
      watchPath: wp.path, targetState: '5-human-review', requiresIntegration: false, fixture: true, agent: 'codex', cliType: 'codex', promptMarkdown: 'Batch acceptance fixture.' } }));
    let detail;
    await expect.poll(async () => {
      const response = await owner.get(`/api/tasks/${created.id}?watchPath=${encodeURIComponent(wp.path)}`);
      if (response.status() !== 200) return false;
      detail = await response.json();
      return !!detail.info;
    }).toBe(true);
    const info = detail!.info;
    // Durable runner evidence in the disposable test workspace, never shared Studio data.
    const file = join(info.folderPath, 'task.json');
    const card = JSON.parse(readFileSync(file, 'utf8'));
    card.requiresIntegration = true;
    card.commits = [{ sha, shortSha: sha.slice(0, 8), message: 'Reviewed delivery fixture', filesChanged: files.length, files }];
    writeFileSync(file, JSON.stringify(card));
    writeFileSync(join(info.folderPath, 'code-review-grade-batch.md'), `---\nverdict: concerns\ngrade: B\ncommit: ${sha}\n---\nSmall naming reservation.\n`);
    await expect.poll(async () => {
      const response = await owner.get(`/api/tasks/${created.id}?watchPath=${encodeURIComponent(wp.path)}`);
      const refreshed = await response.json();
      return refreshed.info?.commits?.[0]?.filesChanged;
    }).toBe(files.length);
    items.push({ card: created.id, project: wp.name, targetLane: '6-completed', reason: 'Owner accepts the reviewed naming tradeoff', reservation: 'Small naming reservation' });
  }
  const batchPath = '/api/auth/operator-session/batches';
  const dpop = proofFor(issued.token, batchPath);
  const submitted = await ok(await anonymous.post(batchPath, { headers: { Authorization: `Bearer ${issued.token}`, DPoP: dpop },
    data: { class: 'cards.accept-override', items, instructionSource: 'owner-chat' } }));
  expect(submitted.items.map((item: { refusal: string | null }) => item.refusal)).toEqual(Array(9).fill(null));
  await page.context().addCookies([
    { name: '__Host-agentstudio-session', value: session, url: baseURL, secure: true, httpOnly: true, sameSite: 'Strict' },
    { name: '__Host-agentstudio-csrf', value: bootstrap.csrfToken, url: baseURL, secure: true, httpOnly: false, sameSite: 'Strict' },
  ]);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(baseURL);
  await page.getByTestId('studio-attention-trigger').click({ timeout: 45000 });
  await expect(page.getByTestId('attention-group-approval').getByTestId('attention-count-in-group')).toHaveText('1');
  await page.getByTestId('attention-group-approval').getByRole('button', { name: 'Review approval', exact: true }).click();
  const sheet = page.getByTestId('operator-batch-sheet');
  await expect(sheet).toBeVisible({ timeout: 45000 });
  await expect(sheet.getByRole('checkbox')).toHaveCount(9);
  for (const box of await sheet.getByRole('checkbox').all()) await expect(box).toBeChecked();
  await sheet.getByRole('checkbox').first().uncheck();
  await expect(sheet.getByRole('button', { name: 'Confirm 8 items', exact: true })).toBeInViewport();
  await sheet.getByRole('checkbox').first().check();
  for (const round of [process.env.BATCH_CAPTURE_ROUND ?? 'round1']) {
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: 844 });
      for (const theme of ['light', 'dark'] as const) {
        await setTheme(page, theme);
        await page.screenshot({ path: join(results, `${round}-${width}-${theme}--real.png`), animations: 'disabled' });
        const documentOverflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
        const sheetOverflow = await sheet.evaluate(element => element.scrollWidth > element.clientWidth);
        viewportChecks.push({ width, theme, documentOverflow, sheetOverflow });
        expect(documentOverflow || sheetOverflow).toBe(false);
      }
    }
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByTestId('operator-batch-sheet-close').click();
  await expect(page).not.toHaveURL(/batch=/);
  await page.goto(`${baseURL}/#/workspace/settings/access?batch=${submitted.id}`);
  await expect(sheet).toBeVisible({ timeout: 45000 });
  await expect(sheet.getByRole('checkbox')).toHaveCount(9);
  await expect(sheet.getByRole('button', { name: 'Confirm 9 items', exact: true })).toBeInViewport();
  let steps = 0;
  page.on('request', req => { if (req.url().endsWith('/api/auth/step-up')) steps++; });
  // Wait for the next unused authenticator timestep; the terminal confirmation used a recovery code.
  const step = Math.floor(Date.now() / 30000);
  await expect.poll(() => Math.floor(Date.now() / 30000), { timeout: 35000 }).toBeGreaterThan(step);
  await sheet.getByLabel('Authenticator code').fill(totp(enrollment.secret));
  await sheet.getByRole('button', { name: 'Confirm 9 items', exact: true }).click();
  await expect(sheet.getByText('Batch confirmed. This batch cannot be confirmed again.')).toBeVisible({ timeout: 60000 });
  expect(steps).toBe(1);
  for (const item of items) {
    await expect.poll(async () => {
      const detail = await (await owner.get(`/api/tasks/${item.card}?project=${encodeURIComponent(wp.name)}`)).json();
      return detail.info?.state;
    }, { timeout: 30000 }).toBe('6-completed');
    const timeline = await (await owner.get(`/api/tasks/${item.card}/timeline?project=${encodeURIComponent(wp.name)}`)).json();
    expect(JSON.stringify(timeline)).toContain(submitted.id);
    expect(JSON.stringify(timeline)).toContain('approvedBy');
    expect(JSON.stringify(timeline)).toContain(`operator-agent:${agent.id}/session:${issued.sessionId}`);
    expect(JSON.stringify(timeline)).toContain(`human:${bootstrap.user.id}`);
    expect(JSON.stringify(timeline)).toContain(item.reason);
  }
  const resultPath = `${batchPath}/${submitted.id}`;
  const result = await ok(await anonymous.get(resultPath, { headers: { Authorization: `Bearer ${issued.token}`,
    DPoP: proofFor(issued.token, resultPath, 'GET') } }));
  expect(result.state).toBe('confirmed');
  expect(result.approvedBy).toBe(bootstrap.user.id);
  expect(result.items.map((item: { state: string }) => item.state)).toEqual(Array(9).fill('executed'));
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await page.screenshot({ path: join(results, `${process.env.BATCH_CAPTURE_ROUND ?? 'round2'}-390-${theme}-confirmed--real.png`), animations: 'disabled' });
  }
  const attention = await (await owner.get('/api/operator/attention')).json();
  expect(attention.items.filter((item: { type: string }) => item.type === 'operator-approval.pending')).toHaveLength(0);
  await page.getByTestId('operator-batch-sheet-close').click();
  await page.getByTestId('studio-attention-trigger').click();
  await expect(page.getByTestId('attention-group-approval')).toHaveCount(0, { timeout: 30000 });
  const renderedAttentionRows = await page.getByTestId('attention-row').count();
  await expect(page.getByTestId('studio-attention-count')).toHaveText(String(renderedAttentionRows));
  writeFileSync(join(results, `${process.env.BATCH_CAPTURE_ROUND ?? 'round1'}-browser-checks.json`), JSON.stringify({ browserErrors, failedRequests, viewportChecks, stepUps: steps, completedCards: items.length,
    approvalCountBefore: 1, approvalCountAfter: 0, aggregateCountAfter: renderedAttentionRows }, null, 2));
  expect(browserErrors).toEqual([]);
  expect(failedRequests.filter(request => request.error !== 'net::ERR_ABORTED')).toEqual([]);
  await owner.dispose();
  await anonymous.dispose();
});
