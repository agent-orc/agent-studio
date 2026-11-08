// One scenario execution: sign in, set up, play the eight steps, tear down,
// write the report. Every dependency is injectable so the tests drive the same
// path against a fake Studio, a fake browser and a fake clock.
import { openBrowser } from './browser.mjs';
import { SCENARIO, readSignIn } from './config.mjs';
import { buildReport, writeReport } from './report.mjs';
import { runSteps, systemClock } from './runner.mjs';
import { SessionClient } from './session.mjs';
import { LifecycleApi } from './studio-api.mjs';
import { scenarioSteps, setupSteps, teardownSteps } from './steps.mjs';

export async function playScenario(config, {
  clock = systemClock,
  log = () => {},
  signIn = readSignIn,
  fetchImpl = fetch,
  browserFactory = openBrowser,
} = {}) {
  const startedAt = clock.now();
  const session = new SessionClient({ url: config.studioUrl, fetchImpl });
  const secrets = [];
  const context = {
    config, clock, log,
    api: new LifecycleApi(session),
    browser: null,
    browserFactory,
    credentials: null,
    // Run-scoped names keep a rerun free of the previous run's repository.
    runId: startedAt.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'z').toLowerCase(),
    state: { workspaceId: null, projectId: null, repositoryUrl: null, addresses: {}, cards: {}, artifacts: {} },
  };

  const steps = [];
  const record = row => {
    steps.push(row);
    log(`${row.status.padEnd(7)} ${row.title}${row.reason ? `: ${row.reason}` : ''}`);
  };
  const readQuota = () => context.state.workspaceId && session.csrfToken
    ? context.api.workspaceQuota(context.state.workspaceId)
    : null;

  try {
    context.credentials = signIn(config.userFile);
    secrets.push(context.credentials.password);
    const chain = config.teardown === 'only'
      ? teardownSteps()
      : [...setupSteps(), ...scenarioSteps(), ...(config.teardown === 'after' ? teardownSteps() : [])];
    await runSteps(chain, { context, clock, readQuota, onStep: record });
  } catch (error) {
    const now = clock.now().toISOString();
    record({ id: 'start', title: 'Start the scenario', phase: 'setup', status: 'failed', startedAtUtc: now,
      endedAtUtc: now, durationSeconds: 0, reason: error.message, evidence: {}, screenshots: [], usage: null });
  } finally {
    await context.browser?.close();
    await session.signOut().catch(() => undefined);
  }

  const endedAt = clock.now();
  const report = buildReport({
    scenario: SCENARIO,
    run: {
      runId: context.runId,
      studioOrigin: session.origin, platform: config.platform, workspace: config.workspace, project: config.project,
      startedAtUtc: startedAt.toISOString(), endedAtUtc: endedAt.toISOString(),
      durationSeconds: Math.round((endedAt - startedAt) / 10) / 100,
    },
    steps,
    addresses: context.state.addresses,
  });
  await writeReport(config.reportDir, report, secrets);
  return report;
}
