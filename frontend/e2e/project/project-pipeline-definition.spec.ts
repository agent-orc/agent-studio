import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect, type Page } from '@playwright/test';
import { setTheme } from '../helpers/theme';

const project = 'website';
const sha = (fill: string) => fill.repeat(64);

const gate = { source: 'commands', build: ['npm run build'], test: ['npm test'], testSuites: ['unit'] };
const environments = [
  {
    path: 'environments[0]', name: 'staging', order: 0, protected: false, automatic: true, approval: [], requires: null,
    steps: [{
      path: 'deploy[0]', kind: 'static-site', artifact: 'site', target: 'web-vm',
      destination: '/srv/www/_staging/studio', healthUrl: 'https://agent-orchestrator.dev/_staging/studio/', healthTimeoutSeconds: 120,
    }],
    variables: [{ path: 'variables[0]', name: 'MAIL_KEY', isSecret: true, secret: 'MAIL_KEY' }],
  },
  {
    path: 'environments[1]', name: 'production', order: 1, protected: true, automatic: false,
    approval: ['Owner', 'Maintainer'], requires: 'staging',
    steps: [{
      path: 'deploy[1]', kind: 'static-site', artifact: 'site', target: 'web-vm',
      destination: '/srv/www/studio', healthUrl: 'https://agent-orchestrator.dev/', healthTimeoutSeconds: 120,
    }],
    variables: [{ path: 'variables[0]', name: 'MAIL_KEY', isSecret: true, secret: 'MAIL_KEY' }],
  },
];
const schemaTwoDiagram = {
  schemaVersion: 2, gate, environments, environmentsDefaulted: false, lifecycleDefaulted: false,
  artifacts: [{ path: 'artifacts[0]', name: 'site', type: 'static', source: 'dist', image: null }],
};
const schemaOne = {
  project, path: '.agent-studio/project.yml', exists: true, schemaVersion: 1, valid: true, issues: [],
  definitionSha256: sha('a'), migrationAvailable: true,
  diagram: { schemaVersion: 1, gate, artifacts: [], environments: [], environmentsDefaulted: false, lifecycleDefaulted: false },
};

async function openSettings(page: Page, definition: object): Promise<void> {
  await page.route('**/api/**', route => route.fulfill({ json: [] }));
  await page.route(/\/api\/v1\/studio\/board/, route => route.fulfill({ json: {
    backlog: [], preparation: [], orchestratorPrep: [], ready: [], progress: [],
    failedPickup: [], codeNotComplete: [], review: [], autoReview: [],
    humanReview: [], completed: [], archive: [],
  } }));
  await page.route('**/api/v1/studio/auth/status', route => route.fulfill({ json: {
    profile: 'local', bootstrapRequired: false, authenticated: true, user: null,
  } }));
  await page.route('**/api/watch-paths**', route => route.fulfill({ json: [
    { name: project, path: '/tasks', rootPath: '/repo', repositoryPath: '/repo' },
  ] }));
  await page.route(`**/api/projects/${project}/workbenches**`, route => route.fulfill({ json: { items: [] } }));
  await page.route(`**/api/projects/${project}/execution`, route => route.fulfill({ json: {
    repositoryDefinition: 'schemaVersion: 1\nstack: [node]', definitionSha256: sha('a'), valid: true, issues: [],
    preparationWarnings: [], lastManifest: null, override: null, source: 'subject-commit',
  } }));
  await page.route(`**/api/projects/${project}/pipeline-definition`, route => route.fulfill({ json: definition }));
  const settings = {
    autoCommit: true, crashRecoveryEnabled: true, autoPushStrategy: 'never',
    pickupMode: 'manual', executionLocation: 'local', integrationBranch: 'develop',
  };
  await page.route('**/api/projects/settings', route => route.fulfill({ json: { [project]: settings } }));
  await page.route(`**/api/projects/${project}/snapshot`, route => route.fulfill({ json: { settings } }));
  await page.route('**/api/v1/studio/runner/status**', route => route.fulfill({ json: { projects: {} } }));
  await page.route('**/api/cli/usage**', route => route.fulfill({ json: { items: [] } }));
  await page.route('**/api/cli/quota**', route => route.fulfill({ json: { snapshots: [] } }));
  // The usage header needs its own shape; unavailable keeps it quiet like other project specs.
  await page.route('**/api/usage/cockpit**', route => route.fulfill({ status: 503 }));
  await page.addInitScript(projectName => {
    localStorage.setItem('atp.studio.tabs.v1', JSON.stringify({
      v: 1,
      tabs: [{ kind: 'board', projectName: '__all__' }, { kind: 'hub', projectName, section: 'settings' }],
      activeKey: `hub:${projectName}`,
    }));
  }, project);
  await page.goto('/');
}

async function capture(page: Page, name: string): Promise<void> {
  const results = process.env['JOB_RESULTS_DIR'];
  expect(results, 'JOB_RESULTS_DIR is required for durable task evidence').toBeTruthy();
  mkdirSync(results!, { recursive: true });
  const section = page.getByTestId('project-pipeline-definition');
  for (const [width, height, theme] of [[1440, 900, 'light'], [1440, 900, 'dark'], [1920, 1080, 'light'], [1920, 1080, 'dark']] as const) {
    await page.setViewportSize({ width, height });
    await setTheme(page, theme);
    await section.scrollIntoViewIfNeeded();
    await section.screenshot({ path: join(results!, `${name}-${width}x${height}-${theme}--mocked.png`) });
  }
}

test('Pipeline settings show validation issues with line and path on the diagram', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openSettings(page, {
    project, path: '.agent-studio/project.yml', exists: true, schemaVersion: 2, valid: false,
    definitionSha256: sha('c'), migrationAvailable: false, diagram: schemaTwoDiagram,
    issues: [
      { path: 'deploy[1].healthTimeoutSeconds', code: 'deploy-health-timeout-invalid',
        message: 'healthTimeoutSeconds must be between 1 and 900.', line: 44 },
      { path: 'variables[1].value', code: 'secret-value-inline',
        message: 'Secret values are refused in project.yml. Store the value in the secret provider and reference it with \'secret: <name>\'.', line: 51 },
    ],
  });

  const section = page.getByTestId('project-pipeline-definition');
  await expect(section.getByTestId('pipeline-validation-status')).toHaveText('2 issues');
  const issues = section.getByTestId('pipeline-validation-issues');
  await expect(issues).toContainText('Line 44');
  await expect(issues).toContainText('deploy[1].healthTimeoutSeconds');
  await expect(issues).toContainText('Secret values are refused');
  await expect(section.getByTestId('pipeline-node-production')).toHaveAttribute('data-issues', 'true');
  await expect(section.getByTestId('pipeline-node-staging')).toHaveAttribute('data-issues', 'false');
  await expect(section.getByTestId('pipeline-protected-production')).toBeVisible();
  await capture(page, 'pipeline-definition-issues');
});

test('Schema 1 migration is a dry run until the operator confirms it', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  let posts = 0;
  await openSettings(page, schemaOne);
  await page.route(`**/api/projects/${project}/pipeline-definition/migration`, async route => {
    if (route.request().method() === 'GET') {
      await route.fulfill({ json: {
        project, available: true, baseSha256: sha('a'), proposalSha256: sha('b'), valid: true, issues: [],
        proposedYaml: 'schemaVersion: 2\n# Schema 2 deployment definition (proposed by Agent Studio from schema 1).\nproject:\n  id: website\n',
        notes: ['No built bundle directory was found; the static artifact uses \'dist\'. Change it to the directory your build writes.'],
        diagram: schemaTwoDiagram,
      } });
      return;
    }
    posts++;
    expect(route.request().postDataJSON()).toEqual({ baseSha256: sha('a'), proposalSha256: sha('b') });
    await route.fulfill({ json: {
      changed: true, commitSha: '4f1c2e0aa5b6c7d8', pushQueued: true,
      definition: { ...schemaOne, schemaVersion: 2, migrationAvailable: false, definitionSha256: sha('b'), diagram: schemaTwoDiagram },
    } });
  });

  const section = page.getByTestId('project-pipeline-definition');
  await expect(section.getByTestId('pipeline-schema-version')).toHaveText('Schema 1');
  await expect(section.getByTestId('pipeline-node-no-environments')).toContainText('Migrate to schema 2');
  await section.getByTestId('pipeline-migration-preview').click();
  const migration = section.getByTestId('pipeline-migration');
  await expect(migration).toContainText('dry run, nothing is written yet');
  await expect(migration.getByTestId('pipeline-node-production')).toBeVisible();
  await expect(migration.getByTestId('pipeline-migration-notes')).toContainText('dist');
  expect(posts).toBe(0);
  await capture(page, 'pipeline-definition-migration-preview');

  await setTheme(page, 'light');
  await migration.getByTestId('pipeline-migration-confirm').click();
  await expect(section.getByRole('status')).toContainText('commit 4f1c2e0');
  await expect(section.getByTestId('pipeline-schema-version')).toHaveText('Schema 2');
  await expect(section.getByTestId('pipeline-migration')).toHaveCount(0);
  expect(posts).toBe(1);
  await capture(page, 'pipeline-definition-schema-2');
});
