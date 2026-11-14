// One scenario execution: sign in the Owner, set up, play the six steps with
// the three invited people, tear down, write the report. Every dependency is
// injectable so the tests drive the same path against a fake Studio, a fake
// browser and a fake clock.
import { openBrowser } from './browser.mjs';
import { PEOPLE, SCENARIO, readSignIn } from './config.mjs';
import { buildReport, writeReport } from './report.mjs';
import { runSteps, systemClock } from './runner.mjs';
import { SessionClient } from './session.mjs';
import { scenarioSteps, setupSteps, teardownSteps } from './steps.mjs';
import { ProductApi } from './studio-api.mjs';

export async function playScenario(config, {
  clock = systemClock,
  log = () => {},
  signIn = readSignIn,
  fetchImpl = fetch,
  browserFactory = openBrowser,
} = {}) {
  const startedAt = clock.now();
  const session = new SessionClient({ url: config.studioUrl, clientId: 'product-scenario-owner', fetchImpl });
  const context = {
    config, clock, log, fetchImpl,
    owner: new ProductApi(session),
    browser: null,
    credentials: null,
    // Run-scoped logins: Studio disables a user but cannot delete one.
    runId: startedAt.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'z').toLowerCase(),
    people: new Map(),
    refusals: [],
    secrets: [],
    state: { project: null, workspaceId: null, addresses: {}, card: null, preview: null, feedback: null },
  };

  const steps = [];
  const record = row => {
    steps.push(row);
    log(`${row.status.padEnd(7)} ${row.title}${row.reason ? `: ${row.reason}` : ''}`);
  };
  const readQuota = () => context.state.workspaceId && session.signedIn
    ? context.owner.workspaceQuota(context.state.workspaceId)
    : null;

  try {
    context.credentials = signIn(config.userFile);
    context.secrets.push(context.credentials.password);
    context.browser = config.teardown === 'only' ? null : await browserFactory({ studioUrl: config.studioUrl, reportDir: config.reportDir });
    const chain = config.teardown === 'only'
      ? teardownSteps()
      : [...setupSteps(), ...scenarioSteps(), ...(config.teardown === 'after' ? teardownSteps() : [])];
    await runSteps(chain, { context, clock, readQuota, onStep: record });
  } catch (error) {
    const now = clock.now().toISOString();
    record({ id: 'start', title: 'Start the scenario', phase: 'setup', actor: null, status: 'failed', startedAtUtc: now,
      endedAtUtc: now, durationSeconds: 0, reason: error.message, evidence: {}, screenshots: [], usage: null });
  } finally {
    await context.browser?.close();
    for (const person of context.people.values()) await person.api.session.signOut().catch(() => undefined);
    await session.signOut().catch(() => undefined);
  }

  const endedAt = clock.now();
  const report = buildReport({
    scenario: SCENARIO,
    run: {
      runId: context.runId,
      studioOrigin: session.origin, platform: config.platform, project: config.project,
      startedAtUtc: startedAt.toISOString(), endedAtUtc: endedAt.toISOString(),
      durationSeconds: Math.round((endedAt - startedAt) / 10) / 100,
    },
    steps,
    people: PEOPLE.filter(person => context.people.has(person.id)).map(person => ({
      id: person.id, title: person.title, login: context.people.get(person.id).login,
      role: 'Product', productionApproval: person.productionApproval,
    })),
    feedback: context.state.feedback,
    refusals: context.refusals,
  });
  await writeReport(config.reportDir, report, context.secrets);
  return report;
}
