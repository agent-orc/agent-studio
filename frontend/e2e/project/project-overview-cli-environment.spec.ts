import { test, expect, Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { api } from '../helpers/api';

interface WatchPath { name: string; path: string; rootPath?: string | null; repositoryPath?: string | null }

const SCREENSHOT_DIR = process.env.JOB_RESULTS_DIR ?? path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'project-overview-cli-environment');

let projectName = '';
let projectPath = '';

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

async function openHub(page: Page) {
  await page.goto(`/#/projects/${slugFor(projectName)}/overview`);
  await expect(page.getByTestId('project-shell')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('project-overview-dashboard')).toBeVisible({ timeout: 15_000 });
}

test.beforeAll(async () => {
  fs.mkdirSync(SCREENSHOT_DIR, { recursive: true });
  const paths = await api<WatchPath[]>('/api/watch-paths');
  expect(paths.length).toBeGreaterThan(0);
  const preferred = paths.find(p => /agent.?task/i.test(p.name)) ?? paths[0];
  projectName = preferred.name;
  projectPath = preferred.rootPath || preferred.path;
});

test.beforeEach(async ({ page }) => {
  await page.route('**/api/projects/*/cli-modes', async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        resolved: {
          copilot: { mode: 'yolo', source: 'default', args: [] },
          claude: { mode: 'yolo', source: 'default', args: [] },
          codex: { mode: 'yolo', source: 'default', args: [] },
          gemini: { mode: 'yolo', source: 'default', args: [] },
        },
        overrides: {},
        available: ['yolo', 'workspace-write', 'read-only', 'custom'],
      }),
    });
  });

  await page.route('**/api/projects/*/cli-context-modes', async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        resolved: {
          copilot: { mode: 'clean', source: 'default', supported: false },
          claude: { mode: 'clean', source: 'default', supported: true },
          codex: { mode: 'clean', source: 'default', supported: true },
          gemini: { mode: 'clean', source: 'default', supported: false },
        },
        overrides: {},
        available: ['clean', 'shared'],
      }),
    });
  });

  await page.route('**/api/cli/usage', async route => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        at: '2026-06-22T10:00:00Z',
        sections: [
          {
            cliType: 'claude',
            available: true,
            version: 'claude 9.9.9-test',
            path: 'C:\\Tools\\claude.cmd',
            error: null,
            projects: [{
              projectName,
              rootPath: projectPath,
              sessions: [{
                id: '12345678-90ab-cdef-1234-567890abcdef',
                label: 'project handoff',
                updatedAt: '2026-06-22T09:30:00Z',
                cwd: projectPath,
                lastUsage: null,
                isProjectDefault: false,
                linkedJob: null,
              }],
            }],
          },
          { cliType: 'codex', available: true, version: 'codex 0.200.0-test', path: 'C:\\Tools\\codex.cmd', error: null, projects: [] },
          { cliType: 'gemini', available: false, version: null, path: 'C:\\Tools\\gemini.cmd', error: null, projects: [] },
        ],
      }),
    });
  });
});

test('overview is operator-only and settings owns CLI environment details', async ({ page }) => {
  await openHub(page);

  await expect(page.getByRole('heading', { name: 'Pipeline snapshot' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Queue health' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Lane counts' })).toHaveCount(0);

  const overview = page.getByTestId('project-overview-dashboard');
  await expect(overview).toContainText('Key numbers');
  await expect(overview).toContainText('Project URLs');
  await expect(overview).toContainText('Deployment');
  await expect(overview).toContainText('Wiki & planning');
  await expect(page.getByTestId('project-cli-onboarding-status')).toHaveCount(0);
  await expect(page.getByTestId('project-detail-cli-environment')).toHaveCount(0);
  await expect(overview).not.toContainText('Watch path');
  await expect(overview).not.toContainText('Working directory');
  await expect(overview).not.toContainText('Project sessions');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '00-overview-operator-dashboard--real.png'),
    fullPage: true,
  });

  await page.goto(`/#/projects/${slugFor(projectName)}/settings`);
  await expect(page.getByTestId('project-settings-panel')).toBeVisible({ timeout: 10_000 });

  const cliEnv = page.getByTestId('project-detail-cli-environment');
  await expect(cliEnv).toBeVisible();
  await expect(cliEnv).toContainText('CLI environment');
  await expect(cliEnv).toContainText('2 / 3 CLIs ready');
  await expect(cliEnv).toContainText('1 project session found');

  const claude = page.getByTestId('project-detail-cli-env-claude');
  await expect(claude).toContainText('Claude Code');
  await expect(claude).toContainText('Ready');
  await expect(claude).toContainText('claude 9.9.9-test');
  await expect(claude).toContainText('C:\\Tools\\claude.cmd');
  await expect(claude).toContainText('project handoff');
  await expect(claude).toContainText('platform default');

  await page.screenshot({
    path: path.join(SCREENSHOT_DIR, '01-settings-cli-environment--real.png'),
    fullPage: true,
  });
});
