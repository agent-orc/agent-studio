import { expect, test } from '../fixtures/dev-backend';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { join, resolve } from 'node:path';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

const PROJECT_NAME = 'Embed Demo';
const PROJECT_ID = 'PROJ-995';
const shots = process.env.JOB_RESULTS_DIR
  ? join(process.env.JOB_RESULTS_DIR, 'url-preview')
  : resolve('../results/AGT-2441/url-preview');

test('keeps start, settings, live output, and stop in the embed in both themes', async ({ page, devBackend }) => {
  void devBackend;
  let processState: 'none' | 'starting' | 'running' | 'stopped' = 'none';
  let releaseReadiness = false;
  let settingsSaved = false;
  let portOccupied = true;
  let startAttempted = false;
  const url = {
    id: 'website', label: 'Website', url: 'http://localhost:4202', sortOrder: 0,
    startRule: { command: 'npm run website', cwd: null, port: 4202, source: 'manual' },
  };
  const project = {
    sourceType: 'local-folder', id: PROJECT_ID, displayName: PROJECT_NAME, shortCode: 'EMB',
    workspaceId: 'ws-embed', storageLocation: '/mock/tasks/embed', rootPath: '/mock/repo/embed',
    repositoryPath: '/mock/repo/embed', repositoryUrl: null, sortOrder: 0, archived: false,
    color: null, cliDefault: null, modelDefault: null, createdAt: '2026-07-13T20:00:00Z', urls: [url],
  };
  const openTasks = [
    previewTask('QST-42', '2-ready'),
    previewTask('QST-51', '5-human-review'),
  ];
  const snapshot = () => ({
    started: true, projectId: PROJECT_ID, urlId: url.id, command: url.startRule.command,
    cwd: '/mock/repo/embed', state: processState, processId: 4242,
    startedAtUtc: '2026-07-13T20:00:00Z',
    finishedAtUtc: processState === 'stopped' ? '2026-07-13T20:02:00Z' : null,
    exitCode: processState === 'stopped' ? -1 : null,
    output: processState === 'starting'
      ? ['> ng serve', 'Building application bundles...', 'Generating browser application bundles...']
      : processState === 'running'
        ? ['> ng serve', 'Building application bundles...', 'Local: http://localhost:4202/', 'ready in 92.4 s']
      : ['[studio] Process stopped by operator.'],
  });

  await page.route('http://localhost:4202/**', route => {
    if (processState !== 'running') return route.abort('connectionrefused');
    // The /docs page reports its live URL via the url-preview-embed contract.
    const reporter = new URL(route.request().url()).pathname.startsWith('/docs')
      ? '<script>parent.postMessage({ source: "url-preview-embed", type: "navigation", url: location.href + "#reported" }, "*")</script>'
      : '';
    return route.fulfill({
      status: 200, contentType: 'text/html',
      body: `<main>Embedded website is running</main>${reporter}`,
    });
  });
  await page.route('**/api/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    const json = (body: unknown, status = 200) => route.fulfill({
      status, contentType: 'application/json', body: JSON.stringify(body),
    });
    if (pathname === '/api/v1/studio/auth/status') return json({
      profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
    });
    if (pathname === '/api/watch-paths') return json([{
      name: PROJECT_NAME, path: '/mock/tasks/embed', rootPath: '/mock/repo/embed',
    }]);
    if (pathname === '/api/v1/workspaces') return json([{
      id: 'ws-embed', displayName: 'Product', sortOrder: 0, isDefault: true,
      color: null, createdAt: '2026-07-13T20:00:00Z', projects: [project],
    }]);
    if (pathname === '/api/v1/projects') return json([project]);
    if (pathname === '/api/tasks' || pathname === '/api/tags') return json([]);
    if (pathname === '/api/v1/studio/board') return json({
      backlog: [], preparation: [], ready: [openTasks[0]], progress: [], autoReview: [],
      humanReview: [openTasks[1]], completed: [previewTask('QST-12', '6-completed')], archive: [],
    });
    if (pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (pathname.endsWith('/urls/website/readiness')) return json(processState === 'running'
      ? { kind: 'healthy', statusCode: 200, framePolicy: 'allowed', detail: null, durationMs: 3 }
      : { kind: 'offline', statusCode: null, framePolicy: 'unknown', detail: 'Connection refused.', durationMs: 3 });
    if (pathname.endsWith('/urls/website/diagnostic')) return json(startAttempted && portOccupied
      ? {
          classification: 'port-in-use',
          summary: 'Port 4202 is already in use by marketing-app (PID 9123).',
          recommendedAction: 'Stop the occupying process or configure a different preview port, then Retry.',
          command: url.startRule.command, cwd: '/mock/repo/embed', url: url.url, configuredPort: 4202,
          processCreated: false, exitCode: null, stdoutTail: '', stderrTail: '', timedOut: false,
          portReachable: true, httpStatus: null, contentReady: false, startupFailureReason: 'port-in-use',
          occupyingProcessId: 9123, occupyingProcessName: 'marketing-app',
          iframeReady: null, framePolicy: null, checkedAt: '2026-07-31T12:00:00Z',
        }
      : {
          classification: 'not-started', summary: 'Nothing is accepting connections at the configured preview address.',
          recommendedAction: 'Start the service or review its setup.', command: url.startRule.command,
          cwd: '/mock/repo/embed', url: url.url, configuredPort: 4202, processCreated: false,
          exitCode: null, stdoutTail: '', stderrTail: '', timedOut: false, portReachable: false,
          httpStatus: null, contentReady: false, checkedAt: '2026-07-31T12:00:00Z',
        });
    if (pathname.endsWith('/url-suggestions')) return json([]);
    if (pathname.endsWith('/urls/website/context')) return json({
      projectName: PROJECT_NAME, repositoryName: 'quality-studio', workingDirectory: '/mock/repo/embed',
      repoRoot: '/mock/repo/embed', isRepo: true, branch: 'task/agt-2455-preview-context',
      headSha: 'abcdef0123456789abcdef0123456789abcdef01', headShortSha: 'abcdef01',
      comparisonRef: 'origin/develop', comparisonKind: 'integration', ahead: 2, behind: 1,
      isDirty: false, error: null,
    });
    if (pathname.endsWith('/urls/website/start')) {
      startAttempted = true;
      if (portOccupied) return json({
        error: 'Port 4202 is already in use by marketing-app (PID 9123).',
        command: url.startRule.command,
        cwd: '/mock/repo/embed',
        classification: 'port-in-use',
        occupyingProcessId: 9123,
        occupyingProcessName: 'marketing-app',
      }, 400);
      processState = 'starting';
      return json(snapshot());
    }
    if (pathname.endsWith('/urls/website/process') && request.method() === 'DELETE') {
      processState = 'stopped';
      return json(snapshot());
    }
    if (pathname.endsWith('/urls/website/process')) {
      if (processState === 'starting' && releaseReadiness) processState = 'running';
      return processState === 'none' ? route.fulfill({ status: 204 }) : json(snapshot());
    }
    if (pathname.endsWith('/urls/website') && request.method() === 'PUT') {
      settingsSaved = true;
      return json(project);
    }
    return route.continue();
  });

  await page.setViewportSize({ width: 1440, height: 900 });
  mkdirSync(shots, { recursive: true });
  await page.goto('/#');
  const projectRow = page.getByTestId(`studio-explorer-project-${PROJECT_NAME}`);
  await expect(projectRow).toBeVisible();
  await dismissDevErrorDialog(page);
  const urlRow = page.getByTestId(`studio-explorer-project-url-${PROJECT_NAME}-${url.id}`);
  if (!await urlRow.isVisible().catch(() => false)) await projectRow.click();
  await urlRow.click();

  const offline = page.getByTestId('url-preview-offline');
  const start = page.getByTestId('url-preview-start');
  const open = page.getByTestId('url-preview-failure-open-external');
  await expect(offline).toBeVisible();
  await expect(page.getByTestId('url-preview-repository')).toHaveText('quality-studio');
  await expect(page.getByTestId('url-preview-branch')).toHaveText('task/agt-2455-preview-context');
  await expect(page.getByTestId('url-preview-head')).toHaveText('abcdef01');
  await expect(page.getByTestId('url-preview-integration')).toContainText('2 ahead, 1 behind origin/develop');
  await expect(page.getByTestId('url-preview-tasks')).toContainText('2');
  await page.getByTestId('url-preview-tasks').locator('summary').click();
  await expect(page.getByTestId('url-preview-task-link')).toHaveCount(2);
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(start).toBeVisible();
    await expect(open).toBeVisible();
    expect(await start.evaluate(element => element.tagName)).toBe('BUTTON');
    expect(await open.evaluate(element => element.tagName)).toBe('BUTTON');
    // Regression: the shell's `.studio button` reset (0,1,1) must not flatten
    // the card's action buttons into plain text. The primary keeps its accent
    // fill and padding; the ghost keeps a visible border.
    expect(await start.evaluate(element => getComputedStyle(element).backgroundColor))
      .not.toBe('rgba(0, 0, 0, 0)');
    expect(await start.evaluate(element => getComputedStyle(element).paddingLeft)).not.toBe('0px');
    expect(await open.evaluate(element => getComputedStyle(element).borderTopWidth)).not.toBe('0px');
    if (process.env['JOB_RESULTS_DIR']) {
      await page.screenshot({
        path: path.join(process.env['JOB_RESULTS_DIR'], `preview-context-offline-${theme}--mocked.png`),
        fullPage: true,
      });
    }
  }
  await page.getByTestId('url-preview-tasks').locator('summary').click();

  await page.getByTestId('url-preview-menu').click();
  await expect(page.getByTestId('url-preview-menu-item-start')).toBeVisible();
  await expect(page.getByTestId('url-preview-menu-item-stop')).toBeDisabled();
  await page.getByTestId('url-preview-menu-item-settings').click();
  await page.getByTestId('url-preview-settings-command').fill('npm run website');
  await page.getByTestId('url-preview-settings-cwd').fill('/mock/repo/embed');
  await page.getByTestId('url-preview-settings-save').click();
  await expect.poll(() => settingsSaved).toBe(true);

  await start.click();
  const occupied = page.getByTestId('url-preview-start-failed');
  await expect(occupied).toContainText('Preview port is occupied');
  await expect(occupied).toContainText('marketing-app (PID 9123)');
  await expect(occupied).toContainText('Stop the occupying process');
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await occupied.screenshot({ path: join(shots, `preview-port-occupied-${theme}--mocked.png`) });
  }

  portOccupied = false;
  await page.getByTestId('url-preview-retry').click();
  const console = page.getByTestId('url-preview-process-console');
  await expect(console).toBeVisible();
  await expect(page.getByTestId('url-preview-starting')).toContainText('Console output is active');
  await expect(page.getByTestId('url-preview-process-status')).toContainText('Starting · console active');
  await expect(page.getByTestId('url-preview-process-output')).toContainText('Generating browser application bundles');
  await setTheme(page, 'light');
  await page.getByTestId('url-preview-tab').screenshot({
    path: join(shots, 'preview-starting-console-active-light.png'),
  });
  await setTheme(page, 'dark');
  await page.getByTestId('url-preview-tab').screenshot({
    path: join(shots, 'preview-starting-console-active-dark.png'),
  });

  releaseReadiness = true;
  await expect(page.getByTestId('url-preview-process-output')).toContainText('ready in 92.4 s');
  await expect(page.getByTestId('url-preview-frame')).toBeAttached();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(page.getByTestId('url-preview-context')).toBeVisible();
    if (process.env['JOB_RESULTS_DIR']) {
      await page.screenshot({
        path: path.join(process.env['JOB_RESULTS_DIR'], `preview-context-online-${theme}--mocked.png`),
        fullPage: true,
      });
    }
  }

  // The address bar is a real input: its URL can be selected (copy) and a
  // typed target navigates the embedded preview without touching the registry.
  const addr = page.getByTestId('url-preview-addr-input');
  await expect(addr).toHaveValue(url.url);
  await addr.click();
  await addr.press('ControlOrMeta+a');
  expect(await addr.evaluate(element => {
    const field = element as HTMLInputElement;
    return (field.selectionEnd ?? 0) - (field.selectionStart ?? 0);
  })).toBe(url.url.length);
  await addr.fill(`${url.url}/docs`);
  await addr.press('Enter');
  await expect(page.getByTestId('url-preview-frame')).toHaveAttribute('src', `${url.url}/docs`);
  // The embedded page reported its live URL; the address bar mirrors it while
  // the frame src stays on the mounted URL (display only, no remount).
  await expect(addr).toHaveValue(`${url.url}/docs#reported`);
  await expect(page.getByTestId('url-preview-frame')).toHaveAttribute('src', `${url.url}/docs`);

  await page.getByTestId('url-preview-menu').click();
  await page.getByTestId('url-preview-menu-item-stop').click();
  await expect.poll(() => processState).toBe('stopped');
  await expect(page.getByTestId('url-preview-process-status')).toContainText('Stopped');
  expect(processState).toBe('stopped');
});

function previewTask(key: string, state: string) {
  const id = key.toLowerCase();
  return {
    id, taskKey: `${PROJECT_ID}::${id}`, key, title: `Preview task ${key}`, state, order: 0,
    agent: 'codex', createdAt: '2026-07-29T10:00:00Z', watchPath: '/mock/tasks/embed',
    projectName: PROJECT_NAME, folderPath: `/mock/tasks/embed/${state}/${id}`,
    lastActivity: '2026-07-29T10:00:00Z', enteredLaneAt: '2026-07-29T10:00:00Z',
    sessionName: null, model: null, cliType: null, useOwnSession: null,
    lastUsage: null, execution: null, commit: null,
  };
}
