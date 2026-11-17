import { randomBytes } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test, expect, type Locator, type Page } from '@playwright/test';
import { FIRST_RUN_ORIGIN, prepareFirstRunProject, verifyFirstRunGit } from '../helpers/first-run-setup';
import { setTheme } from '../helpers/theme';

/**
 * AGT-3809 (playthrough AGT-3795): a second project added by repository URL on
 * the local profile never got a server checkout, so its first card escalated
 * with server-checkout-missing and needed a shell on the Studio host.
 */
const results = join(process.env.JOB_RESULTS_DIR ?? 'test-results', 'second-project-server-checkout');
const git = (args: string[], cwd?: string) => {
  const run = spawnSync('/usr/bin/git', args, { cwd, encoding: 'utf8', windowsHide: true });
  expect(run.status, `git ${args.join(' ')}: ${run.stderr}`).toBe(0);
  return run.stdout;
};

/** Publishes `develop` on the shared demo origin, one commit ahead of main. */
function publishDevelop(bare: string): void {
  if (spawnSync('/usr/bin/git', ['--git-dir', bare, 'rev-parse', '--verify', '--quiet', 'refs/heads/develop']).status === 0) return;
  const seed = mkdtempSync(join(tmpdir(), 'second-project-develop-'));
  try {
    git(['clone', '--quiet', bare, seed]);
    git(['checkout', '--quiet', '-b', 'develop'], seed);
    writeFileSync(join(seed, 'DEVELOP.md'), 'Integration line for the second project.\n');
    git(['add', 'DEVELOP.md'], seed);
    git(['-c', 'user.name=Second Project E2E', '-c', 'user.email=second-project@example.invalid',
      'commit', '--quiet', '-m', 'Open develop'], seed);
    git(['push', '--quiet', 'origin', 'develop'], seed);
  } finally { rmSync(seed, { recursive: true, force: true }); }
}

async function capture(page: Page, target: Locator, name: string): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 900 });
  await setTheme(page, 'light');
  await target.screenshot({ path: join(results, `${name}-1280-light.png`) });
  await setTheme(page, 'dark');
  await target.screenshot({ path: join(results, `${name}-1280-dark.png`) });
  await setTheme(page, 'light');
  await page.setViewportSize({ width: 390, height: 844 });
  await target.screenshot({ path: join(results, `${name}-390-light.png`) });
  await page.setViewportSize({ width: 1280, height: 900 });
}

test('a second URL project creates its server checkout from the UI and integrates into develop', async ({ page }) => {
  test.setTimeout(20 * 60_000);
  const workspace = process.env.PW_E2E_WORKSPACE;
  expect(workspace).toBeTruthy();
  mkdirSync(results, { recursive: true });
  await page.setViewportSize({ width: 1280, height: 900 });
  const bare = join(workspace!, 'first-run-origin.git');
  publishDevelop(bare);

  // The existing project: the checklist verifies its access and clones it.
  const first = await prepareFirstRunProject(page, workspace!, 'Checkout First');
  await page.goto('/#/workspace/settings/first-run');
  await verifyFirstRunGit(page);
  await expect(page.getByTestId('first-run-project')).toHaveAttribute('data-state', 'ready');

  // The second project, added next to it by URL only.
  const name = `Checkout Library ${randomBytes(3).toString('hex')}`;
  await page.goto('/');
  await page.getByTestId('studio-workspace-ws-default-add-project').click();
  const dialog = page.getByTestId('onboard-project-dialog');
  await expect(dialog).toBeVisible();
  await dialog.getByTestId('onboard-project-display-name').fill(name);
  await dialog.getByTestId('onboard-project-short-code').fill('CL' + [...randomBytes(2)].map(byte => String.fromCharCode(65 + byte % 26)).join(''));
  await dialog.getByTestId('onboard-project-repository-url').fill(FIRST_RUN_ORIGIN);
  const offer = dialog.getByTestId('onboard-project-create-checkout');
  await expect(offer).toBeChecked();
  await dialog.getByTestId('onboard-project-create-checkout-label').screenshot({ path: join(results, 'create-flow-offer-1280-light.png') });
  // Defer it here, so the notices on Settings, the board, and the Ready card are visible first.
  await offer.uncheck();
  const runner = dialog.getByTestId('onboard-project-runner');
  await expect(runner.locator('option', { hasText: 'first-run-coding' })).toBeAttached({ timeout: 60_000 });
  await runner.selectOption({ label: 'first-run-coding' });
  await dialog.getByTestId('onboard-project-submit').click();
  await expect(dialog).toHaveCount(0, { timeout: 60_000 });

  const created = await page.request.get('/api/projects');
  const second = (await created.json() as { id: string; displayName: string; repositoryPath?: string | null }[])
    .find(project => project.displayName === name)!;
  expect(second.repositoryPath).toBeFalsy();
  const branch = await page.request.put(`/api/projects/${encodeURIComponent(name)}/integration-branch`, {
    headers: { 'X-Client-Id': 'local-default' }, data: { branch: 'develop' },
  });
  expect(branch.ok(), await branch.text()).toBeTruthy();

  // Settings: one actionable notice before the first card runs.
  await page.goto(`/#/projects/${encodeURIComponent(name.toLowerCase().replaceAll(' ', '-'))}/settings`);
  const basics = page.getByTestId('project-basics-card');
  const settingsNotice = basics.getByTestId('server-checkout-notice');
  await expect(settingsNotice).toHaveAttribute('data-state', 'missing', { timeout: 30_000 });
  await expect(settingsNotice).toContainText(FIRST_RUN_ORIGIN);
  await capture(page, settingsNotice, 'settings-notice');
  const verifyNotice = page.getByTestId('project-settings-no-verify-commands').first();
  await expect(verifyNotice).toContainText('repository checkout is missing');
  await expect(verifyNotice).not.toContainText('cannot be derived');
  await expect(verifyNotice.getByTestId('project-settings-verify-example')).toHaveCount(0);
  await expect(verifyNotice.getByRole('link', { name: 'Set the repository checkout path' }))
    .toHaveAttribute('href', '#project-basics-repository-path');
  await expect(page.locator('#project-basics-repository-path')).toBeVisible();
  await capture(page, verifyNotice, 'settings-verify-without-checkout');

  // Board and Ready card show the same notice.
  await page.goto('/');
  await page.getByRole('tab', { name: 'All projects · Board' }).click();
  await page.getByTestId('studio-board-add-task').click();
  await page.getByTestId('create-title').fill('Second project first card');
  await page.getByTestId('create-project-select').selectOption({ label: name });
  await page.getByTestId('create-lane-2-ready').click();
  await page.getByTestId('create-prompt').fill('Create docs/project-overview.md with one sentence describing this demo repository.');
  await page.getByTestId('create-submit').click();
  await expect(page.getByTestId('create-submit')).toHaveCount(0);
  const boardNotice = page.getByTestId('server-checkout-board-notice');
  await expect(boardNotice.getByTestId('server-checkout-notice')).toHaveCount(1);
  await expect(boardNotice).toContainText(name);
  await expect(boardNotice).not.toContainText(first);
  const card = page.locator('[data-testid="lane-scroll-2-ready"] app-task-card, [data-testid="lane-scroll-2-ready"] app-job-card')
    .filter({ hasText: 'Second project first card' }).first();
  const cardNotice = card.getByTestId('server-checkout-notice');
  await expect(cardNotice).toHaveAttribute('data-state', 'missing');
  await capture(page, boardNotice, 'board-notice');
  await card.screenshot({ path: join(results, 'ready-card-1280-light.png') });
  await capture(page, cardNotice, 'ready-card-notice');
  await page.screenshot({ path: join(results, 'board-before-checkout-1280-light.png') });

  // Create the checkout from the Ready card: progress, then the resulting path.
  await cardNotice.getByTestId('server-checkout-create').click();
  const boardState = boardNotice.getByTestId('server-checkout-notice');
  await expect(boardState).toHaveAttribute('data-state', /running|ready/);
  await expect(boardState).toHaveAttribute('data-state', 'ready', { timeout: 120_000 });
  const checkout = (await boardState.getByTestId('server-checkout-path').innerText()).trim();
  expect(checkout).toMatch(new RegExp(`repository-checkouts/${second.id}$`));
  expect(existsSync(join(checkout, '.git'))).toBe(true);
  expect(git(['rev-parse', '--abbrev-ref', 'HEAD'], checkout).trim()).toBe('develop');
  await expect(card.getByTestId('server-checkout-notice')).toHaveCount(0);
  await capture(page, boardState, 'board-checkout-created');

  // The first card integrates into the non-default integration branch.
  await card.click();
  await page.getByTestId('studio-triage-action-run-now').click();
  await expect.poll(() => {
    const read = spawnSync('/usr/bin/git', ['--git-dir', bare, 'show', 'develop:docs/project-overview.md'], { encoding: 'utf8' });
    return read.status === 0 && read.stdout.includes('This small repository verifies the Studio setup journey.');
  }, { timeout: 15 * 60_000, intervals: [5_000] }).toBe(true);
  expect(spawnSync('/usr/bin/git', ['--git-dir', bare, 'cat-file', '-e', 'main:docs/project-overview.md']).status).not.toBe(0);
  await page.screenshot({ path: join(results, 'first-card-integrated-1280-light.png'), fullPage: true });
});
