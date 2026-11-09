import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { setTheme } from '../helpers/theme';

// AGT-3412: the readiness report the server computes about itself, as the first
// section of the Environment view, in both themes. The lines use the server's
// wording for a server shortly after a move.
const line = (id: string, title: string, scope: string, subject: string | null, state: string,
  meaning: string, remedy: string | null = null, command: string | null = null,
  action: Record<string, string> | null = null, configEntry: Record<string, string> | null = null) =>
  ({ id, title, scope, subject, state, meaning, remedy, command, action, configEntry });

const checks = [
  line('git-identity', 'Git identity of the service user', 'server', null, 'pass',
    'Commits are made as Agent Studio <studio@agent-studio.invalid>, from /etc/gitconfig (the image default).'),
  line('committer-identity', 'Committer identity (git commit-tree probe)', 'server', null, 'pass',
    'A probe commit in a scratch repository succeeded, so merges and artifact commits can record a committer.'),
  line('data-volume', 'Data folder on the data volume', 'server', 'workspace', 'pass',
    '/data/workspace is on the mount /data/workspace (ext4), outside the container layer.'),
  line('data-volume', 'Data folder on the data volume', 'server', 'home', 'fail',
    '/data/home is inside the container layer: whatever Studio writes there is lost when the container is recreated.',
    'Mount it from the data directory: add the volume line below under services.api.volumes in compose.override.yaml and recreate the stack.',
    'docker compose up -d', null,
    { file: 'compose.override.yaml', section: 'services.api.volumes', text: '- ${STUDIO_DATA_DIR}/home:/data/home' }),
  line('state-writable', 'State folder writable', 'server', 'workspace', 'pass',
    'Studio created and removed a probe file in /data/workspace.'),
  line('git-locks', 'No stale Git locks', 'server', 'workspace', 'warn',
    '1 Git lock file(s) older than 10 minutes, the oldest 1 h: /data/workspace/.git/index.lock. Git writes in this repository fail while they exist.',
    'Make sure no git process runs in the repository, then remove the stale lock files.',
    'ps -ef | grep git\nrm -f /data/workspace/.git/index.lock'),
  line('gate-worker', 'Gate worker reachable', 'server', 'kori-gate', 'pass',
    'Pull gate worker kori-gate sent a heartbeat less than a minute ago (3 slot(s)).'),
  line('execution-host', 'Execution host enrolled and seen', 'host', 'kori-review-1', 'pass', 'Enrolled and last seen 1 min ago.'),
  line('execution-host', 'Execution host enrolled and seen', 'host', 'agent-runner-02', 'fail',
    'Enrolled, but not seen for 5 h: it claims no cards.',
    'On agent-runner-02, check that the runner service runs and its RUNNER_SERVER_URL points at this server, then restart it; re-enrol it if its credential was revoked.',
    null, { kind: 'route', target: 'execution-hosts' }),
  line('data-volume', 'Data folder on the data volume', 'project', 'Demo', 'pass',
    '/data/projects/demo is on the mount /data/projects (ext4), outside the container layer.'),
  line('repository-visibility', 'Repository visibility known', 'project', 'Demo', 'warn',
    'Not recorded. Remote Review then treats the repository as public and clones it without credentials, which fails for a private repository.',
    "Record it as private or public in the project's repository visibility setting.",
    null, { kind: 'setting', target: 'repository-visibility', project: 'Demo' }),
  line('app-coverage', 'GitHub App coverage', 'project', 'Demo', 'fail',
    'The App is not installed on https://github.com/acme/demo, so hosts get no token for it and clones and pushes fail.',
    'Install the App on this repository: https://github.com/apps/agent-studio/installations/new, then Recheck.',
    null, { kind: 'route', target: 'github-settings' }),
  line('integration-branch', 'Integration branch set', 'project', 'Demo', 'pass',
    'Integrates into main (from origin/HEAD); origin/main is present.'),
  line('integration-lane', 'Integration lane present', 'project', 'Demo', 'warn',
    'The integration lane refs/agent-studio/integration/main is missing, as in a fresh clone. The first integration has to seed it, and fails the card when origin/main is not available at that moment.',
    'Seed it from the published branch.',
    'git -C /data/projects/demo update-ref refs/agent-studio/integration/main origin/main'),
  line('git-locks', 'No stale Git locks', 'project', 'Demo', 'pass', 'No Git lock file older than 10 minutes.'),
];

const report = (computedAt: string, lines = checks) => ({
  computedAt,
  state: lines.some(item => item.state === 'fail') ? 'fail' : lines.some(item => item.state !== 'pass') ? 'warn' : 'pass',
  failing: lines.filter(item => item.state === 'fail').length,
  attention: lines.filter(item => item.state === 'warn' || item.state === 'unknown').length,
  checks: lines,
});

async function mockEnvironment(page: Page) {
  await page.route('**/api/environment/flow', route => route.fulfill({ json: {
    capturedAt: '2026-11-01T12:04:00Z',
    today: { created: 6, delivered: 4, reviewed: 3, integrated: 2, deployed: null, completed: 2, readyToIntegrationMedianMinutes: 150 },
    lanes: [{ lane: '2-ready', entered: 5, left: 4, medianMinutes: 45 }],
    queues: [{ queue: 'Coding', waiting: 3, oldestWaitMinutes: 90, throughputPerHour: 0.2 }],
    waitingReasons: [],
  } }));
  await page.route('**/api/watcher/cases', route => route.fulfill({ json: { cases: [] } }));
  await page.route('**/api/orchestration/decision-receipts?limit=100', route => route.fulfill({ json: [] }));
  await page.route('**/api/v1/management/attempt-visibility', route => route.fulfill({ json: { generatedAt: '2026-11-01T12:04:00Z', attempts: [] } }));
  await page.route('**/api/v1/management/remote-hosts', route => route.fulfill({ json: [] }));
  await page.route('**/api/clients', route => route.fulfill({ json: [] }));
}

test('the environment view lists each readiness check with state, meaning and remedy in both themes', async ({ page }) => {
  test.setTimeout(120_000);
  await mockEnvironment(page);
  let rechecks = 0;
  await page.route('**/api/system/readiness', route => route.fulfill({ json: report('2026-11-01T12:04:00+00:00') }));
  await page.route('**/api/system/readiness/refresh', async route => {
    expect(route.request().method()).toBe('POST');
    rechecks++;
    // The operator mounted /data/home and seeded the lane; the rest is unchanged.
    await route.fulfill({ json: report('2026-11-01T12:09:00+00:00', checks.map(item =>
      item.subject === 'home' || item.id === 'integration-lane'
        ? { ...item, state: 'pass', remedy: null, command: null, configEntry: null, meaning: item.id === 'integration-lane'
          ? 'refs/agent-studio/integration/main is present.' : '/data/home is on the mount /data/home (ext4), outside the container layer.' }
        : item)) });
  });

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await setTheme(page, 'light');
  await page.getByTestId('studio-titlebar-environment').click();
  const panel = page.getByTestId('server-readiness');
  await expect(panel).toBeVisible();
  await expect(panel.locator('h2')).toContainText('3 failing · 3 need attention · checked 12:04 UTC');

  const home = panel.getByTestId('readiness-server:data-volume:home');
  await expect(home).toHaveAttribute('data-state', 'fail');
  await expect(home).toContainText('Failing');
  await expect(home).toContainText('/data/home is inside the container layer');
  // The volume line is YAML for the compose file: a config entry, not a command.
  await expect(home.getByTestId('readiness-config-location')).toHaveText('compose.override.yaml › services.api.volumes');
  await expect(home.getByTestId('readiness-config-text')).toHaveText('- ${STUDIO_DATA_DIR}/home:/data/home');
  await expect(home.getByTestId('readiness-command-text')).toHaveText('docker compose up -d');
  await expect(panel.getByTestId('readiness-project:app-coverage:Demo')).toContainText('Install the App on this repository');
  await expect(panel.getByTestId('readiness-host:execution-host:agent-runner-02')).toContainText('not seen for 5 h');
  await expect(panel.getByTestId('readiness-server:git-identity:')).toHaveCount(0);

  await panel.getByTestId('server-readiness-passing').click();
  await expect(panel.getByTestId('readiness-server:git-identity:')).toContainText('the image default');
  await expect(panel.locator('tbody tr[data-check]')).toHaveCount(checks.length);
  await expect(panel.locator('.readiness__group')).toHaveText(['Server', 'Execution hosts', 'Project Demo']);

  const output = process.env.JOB_RESULTS_DIR ?? join(process.cwd(), 'test-results');
  mkdirSync(output, { recursive: true });
  // Tall enough that the dialog's scroll area holds the whole report.
  await page.setViewportSize({ width: 1440, height: 1500 });
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.locator('html')).toHaveAttribute('data-studio-theme', theme);
    const failingBackground = await home.evaluate(row => getComputedStyle(row).backgroundColor);
    const passingBackground = await panel.getByTestId('readiness-server:git-identity:')
      .evaluate(row => getComputedStyle(row).backgroundColor);
    expect(failingBackground).not.toBe(passingBackground);
    await panel.screenshot({ path: join(output, `server-readiness-${theme}.png`) });
    await page.screenshot({ path: join(output, `server-readiness-environment-1440-${theme}.png`) });
  }
  await page.setViewportSize({ width: 1440, height: 900 });

  await panel.getByTestId('server-readiness-recheck').click();
  await expect(panel.locator('h2')).toContainText('2 failing · 2 need attention · checked 12:09 UTC');
  await expect(home).toHaveAttribute('data-state', 'pass');
  await expect(home).toContainText('None needed');
  expect(rechecks).toBe(1);
  const box = await page.getByTestId('environment-flow').boundingBox();
  expect(box?.width).toBe(1440);
});
