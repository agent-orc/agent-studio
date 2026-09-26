import { mkdir, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { expect, test } from '../fixtures/dev-backend';
import { setTheme } from '../helpers/theme';

// The dev-backend fixture creates a disposable dirty Git repository before boot.
// The pending item below is produced by CrashRecoveryService, not a route mock.
process.env.DEV_RECOVERY_FIXTURE = '1';

test.describe('Crash recovery prompt', () => {
  test.describe.configure({ timeout: 600_000 }); // Cold isolated backend builds can be slow on a loaded runner.
  test('keeps task navigation usable while a real recovery decision stays pending', async ({ page, devBackend }) => {
    const recoveryActions: string[] = [];
    const pageErrors: string[] = [];
    page.on('pageerror', error => pageErrors.push(error.message));
    page.on('request', request => {
      if (request.method() === 'POST' && /\/api\/crash-recovery\/pending\/[^/]+\/(commit|dismiss)$/.test(request.url()))
        recoveryActions.push(request.url());
    });
    const pendingResponse = await page.request.get(`${devBackend.baseUrl}/api/crash-recovery/pending`);
    expect(pendingResponse.ok()).toBe(true);
    const pendingBody = await pendingResponse.json();
    const record = pendingBody.pending.find((item: { files: string[] }) => item.files.includes('recovery-proof.txt'));
    expect(record).toBeTruthy();
    const evidenceDir = process.env['CRASH_RECOVERY_RESULTS_DIR'];
    if (evidenceDir) {
      await mkdir(evidenceDir, { recursive: true });
      await writeFile(join(evidenceDir, 'isolated-recovery-start.json'), JSON.stringify({
        recordId: record.id, createdAt: record.createdAt, detectedAt: record.detectedAt,
        bootId: record.bootId, repoRoot: record.repoRoot, capturedAt: new Date().toISOString(),
      }, null, 2));
    }
    const watchPaths = await (await page.request.get(`${devBackend.baseUrl}/api/watch-paths`)).json();
    const watchPath = watchPaths[0].path as string;
    const ids: string[] = [];
    const keys: string[] = [];
    const titles = ['Recovery navigation first', 'Recovery navigation second'];
    try {
      for (const title of titles) {
        const created = await page.request.post(`${devBackend.baseUrl}/api/tasks`, {
          headers: { 'X-Client-Id': 'local-default' },
          data: { title, watchPath, agent: 'codex', cliType: 'codex', targetState: '1-preparation', fixture: false },
        });
        expect(created.ok(), await created.text()).toBe(true);
        ids.push((await created.json()).id);
        const detail = await (await page.request.get(`${devBackend.baseUrl}/api/tasks/${ids.at(-1)}?watchPath=${encodeURIComponent(watchPath)}`)).json();
        keys.push(detail.info.key);
      }
      await page.goto('/');
      await expect(page.getByTestId('studio-board')).toBeVisible({ timeout: 30_000 });
      const firstCard = page.getByTestId('task-card').filter({ hasText: titles[0] });
      await expect(firstCard).toBeVisible({ timeout: 30_000 });
      await firstCard.click();
      await expect(page).toHaveURL(new RegExp(`/tasks/${keys[0]}`));
      await expect(page.getByTestId('crash-recovery-entry')).toBeVisible();
      await expect(page.getByTestId('crash-recovery-prompt')).toBeHidden();
      const pagerForward = (await page.getByTestId('studio-task-pager-position').innerText()).trim().startsWith('1');
      await page.getByTestId(pagerForward ? 'studio-task-next' : 'studio-task-prev').click();
      await expect(page).toHaveURL(new RegExp(`/tasks/${keys[1]}`));
      await page.keyboard.press(pagerForward ? 'k' : 'j');
      await expect(page).toHaveURL(new RegExp(`/tasks/${keys[0]}`));
      if (process.env['CRASH_RECOVERY_MEASURE'] === '1') {
        await expect(page.getByTestId('overview-title')).toContainText(titles[0]);
        const samples = await page.evaluate(async (taskTitles) => {
          const durations: number[] = [];
          let current = 0;
          for (let i = 0; i < 105; i++) {
            const next = current === 0 ? 1 : 0;
            const button = document.querySelector<HTMLButtonElement>(
              `[data-testid="studio-task-${next === 1 ? 'next' : 'prev'}"]`);
            if (!button) throw new Error('Task pager is missing');
            const start = performance.now();
            button.click();
            await new Promise<void>((resolve, reject) => {
              const matches = () => document.querySelector('[data-testid="overview-title"]')
                ?.textContent?.includes(taskTitles[next]) ?? false;
              const observer = new MutationObserver(() => {
                if (!matches()) return;
                observer.disconnect();
                clearTimeout(timeout);
                resolve();
              });
              const timeout = window.setTimeout(() => {
                observer.disconnect();
                reject(new Error(`Task title did not switch to ${taskTitles[next]}`));
              }, 15_000);
              observer.observe(document.body, { subtree: true, childList: true, characterData: true });
              if (matches()) {
                observer.disconnect();
                clearTimeout(timeout);
                resolve();
              }
            });
            await new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
            if (i >= 5) durations.push(performance.now() - start);
            current = next;
          }
          return durations;
        }, titles);
        expect(samples).toHaveLength(100);
        const sorted = [...samples].sort((a, b) => a - b);
        const percentile = (p: number) => {
          const rank = p * (sorted.length - 1);
          const lower = Math.floor(rank);
          return sorted[lower] + (sorted[Math.ceil(rank)] - sorted[lower]) * (rank - lower);
        };
        if (evidenceDir) {
          await writeFile(join(evidenceDir, 'pending-recovery-navigation-measurement.json'), JSON.stringify({
            capturedAt: new Date().toISOString(), recordId: record.id, bootId: record.bootId,
            environment: 'Isolated dev backend and Chromium browser on Linux runner',
            before: { source: '2026-09-25 Stable browser replay', completedSwitches: 0,
              p95Ms: null, obstruction: 'Automatic recovery modal blocked navigation until operator intervention' },
            after: { cohort: 'warm pager switches with the isolated record pending', warmups: 5,
              samples: samples.length, criterion: 'changed overview title and two animation frames',
              p50Ms: percentile(0.5), p95Ms: percentile(0.95), durationsMs: samples },
            coreBudget: { targetP95Ms: 100, status: 'unverified',
              reason: 'The complete bounded /core endpoint and core-ready paint marker belong to separate implementation slices; this measures the current overview title proxy.' },
          }, null, 2));
        }
        await page.goto(`/#/tasks/${keys[0]}`);
        await expect(page.getByTestId('overview-title')).toContainText(titles[0]);
      }
      await page.goBack();
      await page.goForward();
      const deepLinkPage = await page.context().newPage();
      try {
        await deepLinkPage.goto(`/#/tasks/${keys[1]}`);
        await expect(deepLinkPage).toHaveURL(new RegExp(`/tasks/${keys[1]}`));
        await expect(deepLinkPage.getByTestId('crash-recovery-entry')).toBeVisible();
        await expect(deepLinkPage.getByTestId('crash-recovery-prompt')).toBeHidden();
      } finally {
        await deepLinkPage.close();
      }
      await page.goto('/');
      await page.reload();
      await expect(page.getByTestId('crash-recovery-entry')).toBeVisible();
      await expect(page.getByTestId('crash-recovery-prompt')).toBeHidden();
      await page.getByTestId('crash-recovery-entry').click();
      await expect(page.getByTestId('crash-recovery-prompt')).toBeVisible();
      if (evidenceDir) {
        await mkdir(evidenceDir, { recursive: true });
        await setTheme(page, 'light');
        await page.screenshot({ path: join(evidenceDir, 'isolated-recovery-dialog-light.png') });
        await setTheme(page, 'dark');
        await page.screenshot({ path: join(evidenceDir, 'isolated-recovery-dialog-dark.png') });
      }
      await page.getByTestId('crash-recovery-defer').click();
      await expect(page.getByTestId('crash-recovery-prompt')).toBeHidden();
      await page.reload();
      await expect(page.getByTestId('crash-recovery-entry')).toBeVisible();
      await expect(page.getByTestId('crash-recovery-prompt')).toBeHidden();
      const unchanged = await (await page.request.get(`${devBackend.baseUrl}/api/crash-recovery/pending`)).json();
      expect(unchanged.pending.some((item: { id: string }) => item.id === record.id)).toBe(true);
      expect(recoveryActions).toEqual([]);
      if (evidenceDir) {
        await mkdir(evidenceDir, { recursive: true });
        await setTheme(page, 'light');
        await page.screenshot({ path: join(evidenceDir, 'isolated-recovery-pending-light.png') });
        await setTheme(page, 'dark');
        await page.screenshot({ path: join(evidenceDir, 'isolated-recovery-pending-dark.png') });
        await writeFile(join(evidenceDir, 'isolated-recovery-provenance.json'), JSON.stringify({
          recordId: record.id, createdAt: record.createdAt, detectedAt: record.detectedAt,
          bootId: record.bootId, repoRoot: record.repoRoot, capturedAt: new Date().toISOString(),
          taskIds: ids, taskKeys: keys,
        }, null, 2));
      }
      await page.getByTestId('crash-recovery-entry').click();
      await page.getByTestId('crash-recovery-dismiss').click();
      await expect(page.getByTestId('crash-recovery-entry')).toBeHidden();
      expect(recoveryActions).toHaveLength(1);
      const afterAction = await (await page.request.get(`${devBackend.baseUrl}/api/crash-recovery/pending`)).json();
      expect(afterAction.pending.some((item: { id: string }) => item.id === record.id)).toBe(false);
      const status = spawnSync('git', ['status', '--porcelain=v1'], { cwd: record.repoRoot, encoding: 'utf8' });
      expect(status.status).toBe(0);
      expect(status.stdout).toContain('recovery-proof.txt');
      expect(pageErrors).toEqual([]);
    } finally {
      // A failed backend is torn down with this disposable fixture. Avoid
      // replacing the first browser assertion with a cleanup connection error.
      const health = await page.request.get(`${devBackend.baseUrl}/healthz`).catch(() => null);
      if (health?.ok()) {
        for (const id of ids) {
          await page.request.delete(`${devBackend.baseUrl}/api/tasks/${id}?watchPath=${encodeURIComponent(watchPath)}`, {
            headers: { 'X-Client-Id': 'local-default' },
          });
        }
      }
    }
  });
  test('shows pending startup recovery items and commits only after confirmation', async ({ page, devBackend }) => {
    await expect.poll(async () => (await page.request.get(`${devBackend.baseUrl}/healthz`)).ok()).toBe(true);
    let commitCalled = false;

    await page.route('**/api/crash-recovery/pending', async (route) => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({
        json: {
          pending: [
            {
              id: 'pending-1',
              createdAt: '2026-06-23T10:00:00Z',
              projectName: 'Agent Taskboard',
              jobId: 'AGT-1807',
              repoRoot: 'C:/Projects/agent-taskboard-devspace/agent-taskboard-dev',
              files: ['src/app/example.ts', 'backend/Features/Runner/CrashRecoveryService.cs'],
              message: 'chore(crash-recovery): rescue orphan changes for AGT-1807\n\nRecovered changes.',
              reason: 'Uncommitted changes were found at startup and attributed to AGT-1807.',
              classification: 'review-required',
            },
          ],
        },
      });
    });

    await page.route('**/api/crash-recovery/pending/pending-1/commit', async (route) => {
      commitCalled = true;
      await route.fulfill({
        json: {
          status: 'committed',
          pending: null,
          commitSha: 'abc1234',
          error: null,
        },
      });
    });

    await page.goto('/');

    const dialog = page.getByTestId('crash-recovery-prompt');
    await expect(dialog).toBeHidden();
    await page.getByTestId('crash-recovery-entry').click();
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText('Review recovered working-tree changes')).toBeVisible();
    await expect(dialog.getByText('Agent Taskboard')).toBeVisible();
    await expect(dialog.getByText('AGT-1807', { exact: true })).toBeVisible();
    await expect(dialog.getByText('src/app/example.ts')).toBeVisible();

    await dialog.getByTestId('crash-recovery-commit').click();
    await expect(dialog).toBeHidden();
    expect(commitCalled).toBe(true);
  });

  test('keeps the board interactive while unattributed read-evidence sidecars stay uncommitted', async ({ page, devBackend }) => {
    await expect.poll(async () => (await page.request.get(`${devBackend.baseUrl}/healthz`)).ok()).toBe(true);
    const dismissed: string[] = [];
    await page.route('**/api/crash-recovery/pending', async (route) => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill({
        json: {
          pending: [
            {
              id: 'runner-sidecars',
              createdAt: '2026-07-30T10:00:00Z',
              projectName: 'Coding Agent Runner',
              jobId: null,
              repoRoot: '/workspace/coding-agent-runner',
              files: [
                'docs/system/runner.md.meta.json',
                'docs/operations/setup.md.meta.json',
                'docs/quality/runtime.md.meta.json',
              ],
              message: 'chore(crash-recovery): rescue orphan changes for project Coding Agent Runner',
              reason: 'Uncommitted changes were found at startup with no active job attribution.',
              classification: 'trivial',
            },
            {
              id: 'chat-sidecars',
              createdAt: '2026-07-30T10:00:00Z',
              projectName: 'Coding Agent Chat',
              jobId: null,
              repoRoot: '/workspace/coding-agent-chat',
              files: ['docs/README.md.meta.json'],
              message: 'chore(crash-recovery): rescue orphan changes for project Coding Agent Chat',
              reason: 'Uncommitted changes were found at startup with no active job attribution.',
              classification: 'trivial',
            },
          ],
        },
      });
    });
    await page.route('**/api/crash-recovery/pending/*/dismiss', async (route) => {
      dismissed.push(route.request().url());
      await route.fulfill({
        json: { status: 'dismissed', pending: null, commitSha: null, error: null },
      });
    });

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/');

    await expect(page.getByTestId('crash-recovery-prompt')).toBeHidden();
    const notification = page.getByTestId('notification-info')
      .filter({ hasText: 'Crash recovery found read-evidence sidecars' });
    await expect(notification).toBeVisible();
    await expect(notification).toContainText('4 metadata sidecar files remain uncommitted');
    await expect(notification).toContainText('Coding Agent Runner: 3 changed files');
    await expect(notification).toContainText('Coding Agent Chat: 1 changed file');

    const boardControl = page.getByRole('button', { name: /Add task/i }).first();
    await expect(boardControl).toBeEnabled();
    await boardControl.focus();
    await expect(boardControl).toBeFocused();

    const evidenceDir = process.env['CRASH_RECOVERY_RESULTS_DIR'];
    if (evidenceDir) {
      await mkdir(evidenceDir, { recursive: true });
      await setTheme(page, 'light');
      await page.screenshot({
        path: join(evidenceDir, 'crash-recovery-trivial-board-ready.png'),
        fullPage: false,
      });
      await setTheme(page, 'dark');
      await page.screenshot({
        path: join(evidenceDir, 'crash-recovery-trivial-board-ready-dark.png'),
        fullPage: false,
      });
    }

    await notification.getByTestId('crash-recovery-trivial-dismiss').click();
    await expect(notification).toBeHidden();
    await expect.poll(() => dismissed.length).toBe(2);
  });
});
