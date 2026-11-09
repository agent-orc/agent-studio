import * as fs from 'fs';
import * as path from 'path';
import { test, expect } from '../fixtures/dev-backend';
import { dismissDevErrorDialog, setTheme } from '../helpers/theme';

/**
 * AGT-3581 (finding F6): the Pipeline page showed the configured step model
 * while a different model ran. Each LLM step row now shows the effective
 * route from `GET /api/model-selection/effective` next to the configured one
 * when they differ, with the rule and the reason ("reason not recorded" when
 * the server has none). The aspect review group offers "Apply to all aspect
 * steps" (per-step override route) and "Apply to all projects" (workspace
 * `review-aspect` consumer policy).
 *
 * The catalogue, settings, and model catalogue come from the fixture backend.
 * The effective routes are mocked so the mismatch is deterministic, and the
 * two write routes are captured instead of forwarded, so the spec never
 * changes the project's steps or the workspace policy.
 */

interface WatchPath { name: string }
interface CatalogueStep { id: string; kind: string; phase?: string; usesModel: boolean; cliType?: string | null; resolvedModel?: string | null }
interface StepWrite { pipelineType: string; stepId: string; cliType: string | null; model: string | null; thinkingLevel: string | null }
interface PolicyWrite { primary: { cli: string; model: string | null; thinking: string | null } | null }

const RESULTS = (() => {
  const fromEnv = process.env.JOB_RESULTS_DIR;
  return fromEnv?.trim()
    ? path.join(fromEnv, 'pipeline-effective-model')
    : path.resolve(__dirname, '..', '..', 'playwright-screenshots', 'pipeline-effective-model');
})();

function slugFor(name: string): string {
  return name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

test('pipeline: configured-versus-effective mismatch and aspect bulk model change', async ({ page, devBackend }) => {
  fs.mkdirSync(RESULTS, { recursive: true });
  const paths = await (await fetch(`${devBackend.baseUrl}/api/watch-paths`)).json() as WatchPath[];
  const project = (paths.find(p => /playwright/i.test(p.name)) ?? paths[0])?.name;
  expect(project, 'needs at least one watched project').toBeTruthy();
  const catalogue = await (await fetch(`${devBackend.baseUrl}/api/projects/pipeline-catalogue?projectName=${
    encodeURIComponent(project)}&pipelineType=task`)).json() as { steps: CatalogueStep[] };
  const aspects = catalogue.steps.filter(step => (step.phase ?? '') === 'aspect' && step.usesModel);
  expect(aspects.length, 'the task pipeline has LLM aspect steps').toBeGreaterThan(1);
  const [mismatched, recorded] = aspects;
  const configuredCli = mismatched.cliType || 'claude';
  const configuredModel = mismatched.resolvedModel || 'claude-opus-5';
  const effective = configuredCli === 'codex'
    ? { cliType: 'claude', model: 'claude-opus-5', thinkingLevel: 'high' }
    : { cliType: 'codex', model: 'gpt-6-sol', thinkingLevel: 'high' };

  const stepWrites: StepWrite[] = [];
  const policyWrites: PolicyWrite[] = [];
  const policy = {
    version: 1, caps: {}, costGuardFactor: 2,
    consumers: { 'review-aspect': { route: null, primary: null, fallbacks: 'catalogue', sameClassOnly: true, stickyWithinCard: true, costGuardFactor: null } },
    hysteresis: { returnBelowCapPoints: 10, minDwellMinutes: 30 },
    migration: { inheritedSteps: 0, explicitSteps: 0, projects: 1, applied: true },
  };
  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const json = (body: unknown) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.pathname === '/api/model-selection/effective') {
      expect(url.searchParams.get('project')).toBe(project);
      return json({ project, pipelineType: url.searchParams.get('pipelineType'), routes: [
        { consumer: 'review-aspect', stepId: mismatched.id,
          configured: { cliType: configuredCli, model: configuredModel, thinkingLevel: 'high' },
          effective, rule: 'quota-cap', reason: null },
        { consumer: 'review-aspect', stepId: recorded.id,
          configured: { cliType: recorded.cliType || 'claude', model: recorded.resolvedModel, thinkingLevel: 'high' },
          effective, rule: 'operator-switch', reason: 'Operator switched Claude to its fallback until the window resets.' },
      ] });
    }
    if (url.pathname === '/api/model-selection/policy' && request.method() === 'GET') return json(policy);
    if (url.pathname === '/api/model-selection/policy/consumers/review-aspect' && request.method() === 'PUT') {
      policyWrites.push(request.postDataJSON() as PolicyWrite);
      return json(policy);
    }
    if (url.pathname.endsWith('/pipeline-step') && request.method() === 'PUT') {
      const body = request.postDataJSON() as StepWrite;
      stepWrites.push(body);
      return json({ pipelineSteps: Object.fromEntries(stepWrites.map(write => [write.stepId,
        { cliType: write.cliType, model: write.model, thinkingLevel: write.thinkingLevel }])) });
    }
    if (url.pathname === '/api/v1/studio/auth/status')
      return json({ profile: 'local', bootstrapRequired: false, authenticated: true, user: null });
    if (url.pathname === '/api/crash-recovery/pending') return json({ pending: [] });
    if (url.pathname === '/api/cli/quota') return json({ at: new Date().toISOString(), ttlSeconds: 600, snapshots: [] });
    const response = await route.fetch({ url: `${devBackend.baseUrl}${url.pathname}${url.search}`, timeout: 30_000 });
    return route.fulfill({ response });
  });

  await page.goto(`/#/projects/${slugFor(project)}/pipeline`);
  // `/pipeline` opens Deploy > Run steps, which hosts the pipeline panel.
  await expect(page.getByTestId('project-detail-pipeline')).toBeVisible({ timeout: 15_000 });
  await dismissDevErrorDialog(page);
  const group = page.getByTestId('pipeline-group-aspect');
  await expect(group).toBeVisible();

  // The mismatched row says what runs, what is configured, the rule, and that no reason was recorded.
  const line = page.getByTestId(`pipeline-step-effective-${mismatched.id}`);
  await expect(line).toBeVisible();
  await expect(line).toContainText(`Runs ${effective.cliType} · ${effective.model}`);
  await expect(line).toContainText(`configured ${configuredCli} · ${configuredModel}`);
  await expect(page.getByTestId(`pipeline-step-effective-rule-${mismatched.id}`)).toHaveText('Quota cap');
  await expect(page.getByTestId(`pipeline-step-effective-reason-${mismatched.id}`)).toHaveText('reason not recorded');
  await expect(page.getByTestId(`pipeline-step-effective-reason-${recorded.id}`))
    .toHaveText('Operator switched Claude to its fallback until the window resets.');
  // A step without a contradicting route keeps its configured chip only.
  for (const step of aspects.slice(2)) await expect(page.getByTestId(`pipeline-step-effective-${step.id}`)).toHaveCount(0);

  const bulk = group.getByTestId('aspect-model-bulk-apply');
  await expect(bulk).toBeVisible();
  for (const theme of ['light', 'dark'] as const) {
    await setTheme(page, theme);
    await expect(line).toBeVisible();
    await group.screenshot({ path: path.join(RESULTS, `aspect-group-effective-mismatch--${theme}.png`) });
  }
  await setTheme(page, 'light');

  // Apply to all aspect steps: one step-override PUT per LLM aspect step, all with one route.
  await bulk.getByTestId('aspect-model-apply-steps').click();
  await expect(bulk.getByTestId('aspect-model-bulk-message')).toContainText(`set on ${aspects.length} aspect steps`);
  expect(stepWrites.map(write => write.stepId).sort()).toEqual(aspects.map(step => step.id).sort());
  expect(new Set(stepWrites.map(write => `${write.pipelineType}:${write.cliType}:${write.model}`)).size).toBe(1);
  const applied = stepWrites[0];

  // Apply to all projects: confirmed, then the workspace review-aspect consumer policy.
  await bulk.getByTestId('aspect-model-apply-projects').click();
  await page.getByTestId('confirm-dialog-confirm').click();
  await expect(bulk.getByTestId('aspect-model-bulk-message')).toContainText('Workspace aspect route set');
  expect(policyWrites).toHaveLength(1);
  expect(policyWrites[0].primary).toMatchObject({ cli: applied.cliType, model: applied.model });
  await bulk.screenshot({ path: path.join(RESULTS, 'aspect-bulk-applied--light.png') });
  await setTheme(page, 'dark');
  await bulk.screenshot({ path: path.join(RESULTS, 'aspect-bulk-applied--dark.png') });
});
