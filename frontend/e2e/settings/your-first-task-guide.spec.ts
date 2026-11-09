import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { test, expect, type Page } from '@playwright/test';
import { api } from '../helpers/api';
import { createJob, getJob, moveJob } from '../helpers/jobs';
import { setTheme, type Theme } from '../helpers/theme';

// Walks the surfaces that docs/operations/setup/your-first-task.md describes,
// against the isolated real backend. PW_DOC_SCREENSHOTS=1 refreshes the
// committed guide images; JOB_RESULTS_DIR receives light, dark and 390 px
// review captures. The runner list is the only stubbed response: the isolated
// stack has no runner host, and the guide tells users to pick one.

const DOCS_DIR = resolve(__dirname, '..', '..', '..', 'docs', 'operations', 'setup', 'images', 'your-first-task');
const RESULTS_DIR = process.env.JOB_RESULTS_DIR?.trim() || '';
const WRITE_DOCS = process.env.PW_DOC_SCREENSHOTS === '1';
const RUNNER = 'distributed-runner';
const PROJECT = 'First Task Demo';

interface WatchPath { name: string; path: string }

function demoRepository(workspace: string): string {
  const repository = join(workspace, 'first-task-demo');
  if (existsSync(join(repository, '.git'))) return repository;
  mkdirSync(repository, { recursive: true });
  writeFileSync(join(repository, 'README.md'), '# First task demo\n\nA disposable repository for the first-task guide.\n');
  const origin = join(workspace, 'first-task-demo-origin.git');
  for (const [cwd, args] of [
    [workspace, ['init', '-q', '--bare', origin]],
    [repository, ['init', '-q', '-b', 'develop']], [repository, ['config', 'user.name', 'Guide Fixture']],
    [repository, ['config', 'user.email', 'guide-fixture@example.invalid']], [repository, ['add', '.']],
    [repository, ['commit', '-qm', 'docs: seed readme']], [repository, ['branch', 'main']],
    [repository, ['remote', 'add', 'origin', origin]], [repository, ['push', '-q', 'origin', 'develop', 'main']],
  ] as const) {
    const result = spawnSync('git', [...args], { cwd, encoding: 'utf8' });
    expect(result.status, `git ${args[0]}: ${result.stderr}`).toBe(0);
  }
  return repository;
}

async function stubRunnerHost(page: Page): Promise<void> {
  await page.route('**/api/clients', route => {
    if (route.request().method() !== 'GET') return route.continue();
    const now = new Date().toISOString();
    return route.fulfill({ json: [{
      id: RUNNER, displayName: RUNNER, kind: 'service', registeredAt: now, lastSeenAt: now,
      runnerGitStatus: 'ready', runnerDaemonState: 'running', runnerActiveSlots: 0, runnerAvailableSlots: 2,
    }] });
  });
  await page.route(`**/api/clients/${RUNNER}/telemetry**`, route => route.fulfill({
    json: { clientId: RUNNER, window: '14d', points: [], findings: [] },
  }));
}

async function capture(page: Page, name: string, target?: ReturnType<Page['locator']>): Promise<void> {
  await page.evaluate(() => new Promise<void>(done => requestAnimationFrame(() => requestAnimationFrame(() => done()))));
  const shoot = (path: string) => target
    ? target.screenshot({ path, animations: 'disabled', caret: 'hide' })
    : page.screenshot({ path, animations: 'disabled', caret: 'hide' });
  if (WRITE_DOCS) {
    mkdirSync(DOCS_DIR, { recursive: true });
    await shoot(join(DOCS_DIR, `${name}.png`));
  }
  if (RESULTS_DIR) {
    mkdirSync(join(RESULTS_DIR, 'your-first-task'), { recursive: true });
    await shoot(join(RESULTS_DIR, 'your-first-task', `${name}--${await currentTheme(page)}.png`));
  }
}

async function currentTheme(page: Page): Promise<Theme> {
  return (await page.evaluate(() => document.documentElement.dataset['studioTheme'])) === 'dark' ? 'dark' : 'light';
}

async function reviewCaptures(page: Page, name: string, ready: () => Promise<void>): Promise<void> {
  if (!RESULTS_DIR) return;
  const dir = join(RESULTS_DIR, 'your-first-task');
  mkdirSync(dir, { recursive: true });
  await setTheme(page, 'dark');
  await page.reload();
  await ready();
  await page.screenshot({ path: join(dir, `${name}--dark.png`), animations: 'disabled', caret: 'hide' });
  await page.setViewportSize({ width: 390, height: 844 });
  await setTheme(page, 'light');
  await page.reload();
  await ready();
  await page.screenshot({ path: join(dir, `${name}--390.png`), fullPage: true, animations: 'disabled', caret: 'hide' });
  await page.setViewportSize({ width: 1440, height: 900 });
}

test.describe('your first task guide', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('checklist, project creation, execution location, project.yml notice and Accept match the guide', async ({ page }) => {
    test.setTimeout(240_000);
    const workspace = process.env.PW_E2E_WORKSPACE;
    test.skip(!workspace, 'Runs on the isolated prebuilt stack (npm run e2e).');
    const repository = demoRepository(workspace!);
    await stubRunnerHost(page);
    await page.addInitScript(() => {
      if (!localStorage.getItem('atp.studio.theme')) localStorage.setItem('atp.studio.theme', 'light');
    });

    await page.goto('/');
    const addProject = page.getByTestId('studio-workspace-ws-default-add-project');
    await expect(addProject).toBeVisible();
    await addProject.click();
    const dialog = page.getByTestId('onboard-project-dialog');
    await expect(dialog).toContainText('Create a project');
    await page.getByTestId('onboard-project-display-name').fill(PROJECT);
    const shortCode = page.getByTestId('onboard-project-short-code');
    await shortCode.fill('FTD');
    await page.getByTestId('onboard-project-repository-path').fill(repository);
    await expect(dialog).toContainText('Execution location');
    const runner = page.getByTestId('onboard-project-runner');
    await expect(runner.locator('option', { hasText: 'Local host' })).toHaveCount(1);
    await runner.selectOption(RUNNER);
    await dialog.getByRole('heading', { name: 'Execution location' }).scrollIntoViewIfNeeded();
    await capture(page, 'create-project-dialog');
    // The isolated backend knows no runner principal, so it stores the
    // project on the local host; the guide's choice is captured above.
    await runner.selectOption('local');
    const createdProject = page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/v1/projects', { timeout: 30_000 });
    await page.getByTestId('onboard-project-submit').click();
    const response = await createdProject;
    expect(response.ok(), `${response.status()}: ${await response.text()}`).toBeTruthy();
    await expect(dialog).toBeHidden({ timeout: 30_000 });
    await expect.poll(async () => (await api<WatchPath[]>('/api/watch-paths')).some(entry => entry.name === PROJECT),
      { timeout: 30_000 }).toBe(true);

    const slug = PROJECT.toLowerCase().replace(/[^a-z0-9]+/g, '-');
    const card = page.getByTestId('project-execution-card');
    // A deferred chunk can miss its first load right after the project
    // appears (NG0750); a reload recovers.
    await expect(async () => {
      await page.goto(`/#/projects/${slug}/settings`);
      await page.reload();
      await expect(card).toBeVisible({ timeout: 15_000 });
    }).toPass({ timeout: 90_000 });
    await card.getByTestId('project-pickup-mode-auto').click();
    await expect(card.getByTestId('project-pickup-mode-auto')).toHaveAttribute('aria-pressed', 'true');
    await expect(card).toContainText('Ready tasks are offered automatically.');
    // Auto on the orchestrator's own host is refused in production builds.
    await expect(card.getByTestId('local-coding-disabled-warning')).toContainText('Local coding is disabled');
    await card.scrollIntoViewIfNeeded();
    await capture(page, 'execution-location-auto', card);

    const notice = page.getByTestId('project-settings-no-verify-commands').first();
    await expect(notice).toContainText('No verify commands defined');
    await expect(notice).toContainText('.agent-studio/project.yml');
    await expect(page.getByTestId('project-settings-verify-example').first()).toContainText('schemaVersion: 1');
    await notice.scrollIntoViewIfNeeded();
    await capture(page, 'project-yml-missing', notice);
    await reviewCaptures(page, 'project-settings', () => expect(page.getByTestId('project-execution-card')).toBeVisible({ timeout: 30_000 }));

    await page.goto('/#/workspace/settings/first-run');
    const checklist = page.getByTestId('first-run-checklist');
    await expect(checklist).toBeVisible();
    await expect(checklist).toContainText('First-run checklist');
    for (const step of ['Owner account', 'Provider login on an execution host', 'GitHub read and write access',
      'First project', 'Execution host', 'Proof run']) {
      await expect(checklist).toContainText(step);
    }
    await expect(page.getByTestId('first-run-create-proof')).toHaveText(/Create sample card/);
    await expect(page.getByTestId('first-run-refresh')).toHaveText('Refresh', { timeout: 30_000 });
    await capture(page, 'first-run-checklist');
    await reviewCaptures(page, 'first-run-checklist', () => expect(checklist).toBeVisible());

    const watchPath = (await api<WatchPath[]>('/api/watch-paths')).find(entry => entry.name === PROJECT);
    expect(watchPath, 'the new project is registered').toBeTruthy();
    const created = await createJob({
      title: 'docs: add a project overview', watchPath: watchPath!.path, targetState: '2-ready',
      mode: 'concept', requiresIntegration: false,
      promptMarkdown: 'Write docs/project-overview.md from the README. End with [[TASK_DONE]].',
    });
    await expect.poll(async () => (await getJob(created.id, watchPath!.path).catch(() => null))?.id).toBe(created.id);
    await api(`/api/tasks/${encodeURIComponent(created.id)}/concept-dossier?watchPath=${encodeURIComponent(watchPath!.path)}`, {
      method: 'POST', body: JSON.stringify({ noDossierNeeded: true, reason: 'Guide fixture without code.' }),
    });
    await moveJob(created.id, watchPath!.path, '5-human-review');
    await page.goto(`/?job=${encodeURIComponent(created.id)}&watchPath=${encodeURIComponent(watchPath!.path)}`);
    const title = page.getByTestId('overview-title');
    await expect(title).toContainText('docs: add a project overview', { timeout: 30_000 });
    const accept = page.getByTestId('studio-triage-action-mark-done');
    await expect(accept).toContainText('Accept');
    await expect(accept).toBeEnabled({ timeout: 30_000 });
    await capture(page, 'human-review-accept');
    // Clicking Accept is covered by triage-actions-in-detail-header.spec.ts; a
    // card moved to Human review through the API is parked for an operator.
    await reviewCaptures(page, 'human-review-accept', () => expect(title).toContainText('docs: add a project overview', { timeout: 30_000 }));
  });
});
