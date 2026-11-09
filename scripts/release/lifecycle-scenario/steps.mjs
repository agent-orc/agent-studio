// The steps of the application-lifecycle first run (Dossier
// application-lifecycle §8, card AGT-3178). Each step acts through the Studio
// API or the browser, records what it saw, and throws a StepFailure with the
// evidence when Studio does not do what the step expects.
import { evaluateAcceptance } from '../../../tools/studio-op/src/accept-policy.mjs';
import { StepFailure, StepSkipped, expect, waitFor } from './runner.mjs';
import { totp } from './session.mjs';

const INTEGRATED = new Set(['6-completed', '7-archive']);
const HUMAN_REVIEW = '5-human-review';
const BACKLOG = '0-backlog';
const READY = '2-ready';
const CHAT_CARD_WAIT_MS = 10 * 60_000;
const CHAT_CONFIRM_AFTER_MS = 3 * 60_000;
const CHAT_CONFIRMATION = 'Yes, please create the cards for this now.';
// Stage and production of one project.
const HOSTING_SLOTS = 2;
const FAILURE_CARD_TAGS = ['production-deployment', 'environment-health'];
const CARD_IDS = Object.freeze({
  environments: 'lifecycle-environments',
  fault: 'lifecycle-seeded-fault',
  fix: 'lifecycle-fix-production-health',
});

export function setupSteps() {
  return [
    { id: 'sign-in-api', phase: 'setup', title: 'Sign in the simulated user through the API', run: signInApi },
    { id: 'workspace', phase: 'setup', title: 'Set up the workspace with a one-week quota', needs: ['sign-in-api'], run: setUpWorkspace },
  ];
}

export function scenarioSteps() {
  return [
    { id: 'browser-sign-in', title: '1. Sign in to Studio', needs: ['sign-in-api'], run: browserSignIn },
    { id: 'create-project', title: '2. Create the project from the static web app template', needs: ['workspace', 'browser-sign-in'], run: createProject },
    { id: 'declare-environments', phase: 'setup', title: 'Declare stage and production through a card', needs: ['create-project'], run: declareEnvironments },
    { id: 'wish', title: '3. Write the wish in the orchestrator chat; cards are created', needs: ['declare-environments'], run: writeWish },
    { id: 'coding', title: '4. Coding, review and gates run; the quota panel shows use', needs: ['wish'], run: runCoding },
    { id: 'stage', title: '5. The gate-approved artifact reaches stage; the user checks it', needs: ['coding'], run: checkStage },
    { id: 'production', title: '6. The user approves; the same artifact reaches production', needs: ['stage'], run: approveProduction },
    { id: 'production-failure', title: '7a. A seeded bad change fails production health; rollback and card follow', needs: ['production'], run: failProduction },
    { id: 'fix', title: '7b. The fix goes through stage to production again', needs: ['production-failure'], run: fixProduction },
    { id: 'quota-warning', title: '8. A quota below the remaining work shows the warning and holds new work', needs: ['coding'], run: lowerQuota },
  ];
}

export function teardownSteps() {
  return [
    { id: 'stop-environments', phase: 'teardown', always: true, title: 'Stop stage and production', run: stopEnvironments },
    { id: 'remove-project', phase: 'teardown', always: true, title: 'Remove the project', run: removeProject },
    { id: 'remove-repository', phase: 'teardown', always: true, title: 'Remove the repository', run: removeRepository },
    { id: 'remove-workspace', phase: 'teardown', always: true, title: 'Remove the workspace', run: removeWorkspace },
  ];
}

// ----- setup -----

async function signInApi(ctx, record) {
  const user = await signIn(ctx);
  record.note('login', ctx.credentials.login);
  record.note('role', user?.role ?? 'unknown');
  expect(user?.role === 'owner',
    'The simulated user must be the Owner: template projects and quota limits are Owner actions.', { role: user?.role });
}

async function setUpWorkspace(ctx, record) {
  const { api, config, state } = ctx;
  const workspaces = await api.workspaces();
  const elsewhere = workspaces.flatMap(ws => (ws.projects ?? []).map(project => ({ ws, project })))
    .find(({ ws, project }) => project.displayName === config.project && ws.displayName !== config.workspace);
  expect(!elsewhere, `A project named "${config.project}" already exists in workspace "${elsewhere?.ws.displayName}".`);
  let workspace = workspaces.find(ws => ws.displayName === config.workspace);
  if (workspace) {
    const foreign = (workspace.projects ?? []).filter(project => project.displayName !== config.project);
    expect(foreign.length === 0, `Workspace "${config.workspace}" holds other projects; the scenario needs its own workspace.`,
      { projects: foreign.map(project => project.displayName) });
    // An earlier run that did not tear down leaves its project; remove it so this run starts clean.
    const leftovers = (workspace.projects ?? []).filter(project => project.displayName === config.project);
    for (const project of leftovers) {
      await stopAll(ctx, project.displayName);
      await api.deleteProject(project.id);
    }
    record.note('reusedWorkspace', true);
    record.note('removedLeftoverProjects', leftovers.map(project => project.id));
  } else {
    workspace = await api.createWorkspace(config.workspace);
    record.note('reusedWorkspace', false);
  }
  state.workspaceId = workspace.id;
  record.note('workspaceId', workspace.id);
  const limits = {
    modelTokensPerWeek: config.quota.modelTokensPerWeek,
    computeCoreHoursPerWeek: config.quota.computeCoreHoursPerWeek,
    hostingSlots: HOSTING_SLOTS,
  };
  await api.setWorkspaceQuota(workspace.id, limits);
  const quota = await api.workspaceQuota(workspace.id);
  record.note('limits', quota.limits);
  record.note('week', `${quota.weekStartUtc} to ${quota.weekEndUtc}`);
  record.note('status', quota.status);
  expect(quota.limits?.modelTokensPerWeek === limits.modelTokensPerWeek
      && Number(quota.limits?.computeCoreHoursPerWeek) === limits.computeCoreHoursPerWeek,
    'The workspace quota does not show the limits the scenario set.', { limits: quota.limits });
}

// ----- scenario -----

async function browserSignIn(ctx, record) {
  const { config, credentials } = ctx;
  ctx.browser = await ctx.browserFactory({ studioUrl: config.studioUrl, reportDir: config.reportDir });
  await ctx.browser.signIn({ login: credentials.login, password: credentials.password, totpCode: await freshTotp(ctx) });
  const gate = await ctx.browser.testIdState('auth-gate', 2_000);
  record.screenshot(await ctx.browser.screenshot('signed-in'), 'Studio after sign-in');
  expect(!gate.visible, 'The sign-in form is still shown after signing in.');
}

async function createProject(ctx, record) {
  const { api, config, state } = ctx;
  const repository = `${config.slug}-${ctx.runId}`;
  const created = await api.createProject({
    workspaceId: state.workspaceId,
    displayName: config.project,
    createRepository: { owner: config.repositoryOwner, name: repository, visibility: 'private', template: config.template },
  });
  state.projectId = created.id;
  state.repositoryUrl = created.repositoryUrl ?? null;
  record.note('projectId', created.id);
  record.note('shortCode', created.shortCode);
  record.note('template', config.template);
  record.note('repository', state.repositoryUrl ?? `${config.repositoryOwner}/${repository}`);
  expect(created.id, 'Studio did not return a project id.');
  expect(state.repositoryUrl, 'Studio did not report the created repository.');
  // Cards that exist before the wish (the template's first card) are not the wish's cards.
  state.initialCardIds = (await api.tasks(created.id)).map(card => card.id);
  record.note('templateCards', state.initialCardIds);
  await shoot(ctx, record, `/#/projects/${created.id}/overview`, 'project-created', 'New project overview');
}

async function declareEnvironments(ctx, record) {
  const { api, config, state } = ctx;
  const card = await createCard(ctx, CARD_IDS.environments, 'Declare stage and production', environmentsPrompt(config));
  record.note('card', card.key);
  state.cards.environments = card;
  await driveCards(ctx, record, [card], ctx.config.codingTimeoutMs);
  const declared = await waitFor('stage and production declarations', async () => {
    const view = await api.environments(config.project).catch(() => null);
    const stage = environment(view, 'stage');
    const production = environment(view, 'production');
    return stage && production ? { stage, production } : null;
  }, poll(ctx, ctx.config.releaseTimeoutMs));
  state.addresses.stage = declared.stage.declaration.address;
  state.addresses.production = declared.production.declaration.address;
  record.note('stage', `${declared.stage.declaration.id} on ${declared.stage.declaration.targetHostId}: ${state.addresses.stage}`);
  record.note('production', `${declared.production.declaration.id} on ${declared.production.declaration.targetHostId}: ${state.addresses.production}`);
}

async function writeWish(ctx, record) {
  const { api, config, state, clock } = ctx;
  const known = new Set([...state.initialCardIds, ...Object.values(state.cards).map(card => card.id)]);
  const reply = await api.chat(config.project, config.wish);
  record.note('wish', config.wish);
  record.note('reply', excerpt(reply?.reply?.text));
  const started = clock.now().getTime();
  let confirmed = false;
  const cards = await waitFor('cards from the chat wish', async () => {
    const fresh = (await api.tasks(state.projectId)).filter(card => !known.has(card.id));
    if (fresh.length) return fresh;
    if (!confirmed && clock.now().getTime() - started >= CHAT_CONFIRM_AFTER_MS) {
      // The orchestrator may propose first and create cards after a confirmation.
      confirmed = true;
      const answer = await api.chat(config.project, CHAT_CONFIRMATION);
      record.note('confirmation', excerpt(answer?.reply?.text));
    }
    return null;
  }, poll(ctx, CHAT_CARD_WAIT_MS));
  state.cards.wish = cards.map(card => ({ id: card.id, key: card.key, title: card.title }));
  record.note('cards', state.cards.wish.map(card => `${card.key}: ${card.title}`));
  await shoot(ctx, record, `/#/projects/${state.projectId}/board`, 'wish-cards', 'Board with the cards from the wish');
}

async function runCoding(ctx, record) {
  const { api, state } = ctx;
  await driveCards(ctx, record, state.cards.wish, ctx.config.codingTimeoutMs);
  const quota = await api.workspaceQuota(state.workspaceId);
  record.note('quotaUsage', quota.usage);
  record.note('quotaRemaining', quota.remaining);
  record.note('quotaStatus', quota.status);
  expect(quota.usage.modelTokens > 0 || quota.usage.computeCoreHours > 0,
    'The workspace quota shows no use after coding, review and gates.', { meterComplete: quota.meterComplete });
  await ctx.browser.openStudio(`/#/projects/${state.projectId}/settings`);
  const panel = await ctx.browser.testIdState('workspace-quota');
  record.screenshot(await ctx.browser.screenshot('quota-panel'), 'Quota panel after coding');
  expect(panel.visible, 'The quota panel is not shown in the project settings.');
  record.note('quotaPanel', excerpt(panel.text));
}

async function checkStage(ctx, record) {
  const { state } = ctx;
  const staged = await waitForStage(ctx, keys(state.cards.wish));
  state.artifacts.feature = staged.artifact;
  noteRelease(record, staged);
  const page = await ctx.browser.openAddress(state.addresses.stage);
  record.note('stageHttpStatus', page.status);
  expect(page.status === 200, `The stage address answered ${page.status}.`);
  const names = await ctx.browser.buttonNames();
  record.note('buttons', names);
  const reset = names.find(name => /reset/i.test(name));
  expect(reset, 'The stage page has no reset button.');
  const counter = names.find(name => !/reset/i.test(name));
  if (counter) {
    await ctx.browser.clickButton(counter);
    record.note('afterTwoClicks', excerpt(await ctx.browser.clickButton(counter)));
  }
  record.screenshot(await ctx.browser.screenshot('stage-app'), 'Click Counter on stage');
  record.note('afterReset', excerpt(await ctx.browser.clickButton(reset)));
  await shoot(ctx, record, `/#/projects/${state.projectId}/deploy`, 'stage-deploy-area', 'Deploy area with the pending production approval');
}

async function approveProduction(ctx, record) {
  const { state } = ctx;
  const artifact = state.artifacts.feature;
  const production = await promote(ctx, record, artifact);
  expect(production.deployedArtifact === artifact, 'Production does not run the artifact that stage ran.',
    { stage: artifact, production: production.deployedArtifact });
  record.note('sameDigestAsStage', true);
  const page = await ctx.browser.openAddress(state.addresses.production);
  record.note('productionHttpStatus', page.status);
  expect(page.status === 200, `The production address answered ${page.status}.`);
  record.screenshot(await ctx.browser.screenshot('production-app'), 'Click Counter on production');
}

async function failProduction(ctx, record) {
  const { api, config, state } = ctx;
  const healthy = state.artifacts.feature;
  const before = new Set((await api.tasks(state.projectId)).map(card => card.id));
  const fault = await createCard(ctx, CARD_IDS.fault, 'Seeded fault: remove the production health file', faultPrompt());
  state.cards.fault = fault;
  record.note('seededCard', fault.key);
  await driveCards(ctx, record, [fault], config.codingTimeoutMs);
  const staged = await waitForStage(ctx, [fault.key]);
  state.artifacts.bad = staged.artifact;
  record.note('badArtifact', staged.artifact);
  await api.approvePromotion(config.project, staged.artifact);
  record.note('approvedBadArtifact', true);
  const rolledBack = await waitFor('the failed production deployment and its rollback', async () => {
    const view = await api.environments(config.project);
    const production = environment(view, 'production');
    const failed = production?.history.find(item => item.artifact === staged.artifact && item.status === 'failed');
    if (!failed) return null;
    return production.deployedArtifact === healthy && production.history[0]?.status === 'rolled-back'
      ? { production, failed, failureCard: view.failureCards.find(item => item.artifact === staged.artifact) } : null;
  }, poll(ctx, config.deployTimeoutMs * 2));
  record.note('failedEvidence', rolledBack.failed.evidence);
  record.note('rolledBackTo', rolledBack.production.deployedArtifact);
  expect(rolledBack.failureCard, 'The environments view lists no failure for the bad artifact.');
  const card = await waitFor('the production failure card', async () => {
    const fresh = (await api.tasks(state.projectId))
      .filter(item => !before.has(item.id) && (item.tags ?? []).some(tag => FAILURE_CARD_TAGS.includes(tag)));
    return fresh[0] ?? null;
  }, poll(ctx, config.deployTimeoutMs));
  state.cards.failure = { id: card.id, key: card.key, title: card.title };
  record.note('failureCard', `${card.key}: ${card.title}`);
  await shoot(ctx, record, `/#/projects/${state.projectId}/deploy`, 'production-rollback', 'Deploy area after the rollback');
}

async function fixProduction(ctx, record) {
  const { config, state } = ctx;
  const fix = await createCard(ctx, CARD_IDS.fix, 'Restore the production health file', fixPrompt(state.cards.failure));
  state.cards.fix = fix;
  record.note('fixCard', fix.key);
  await driveCards(ctx, record, [fix], config.codingTimeoutMs);
  const staged = await waitForStage(ctx, [fix.key]);
  state.artifacts.fix = staged.artifact;
  noteRelease(record, staged);
  const production = await promote(ctx, record, staged.artifact);
  record.note('productionArtifact', production.deployedArtifact);
  const health = await ctx.browser.openAddress(`${state.addresses.production}/health.txt`);
  record.note('healthHttpStatus', health.status);
  expect(health.status === 200, `The production health file answered ${health.status}.`);
  await shoot(ctx, record, `/#/projects/${state.projectId}/deploy`, 'production-fixed', 'Deploy area after the fix');
}

async function lowerQuota(ctx, record) {
  const { api, config, state } = ctx;
  const before = await api.workspaceQuota(state.workspaceId);
  // A limit at the tokens already used leaves nothing for the remaining work.
  const limits = {
    modelTokensPerWeek: Math.max(1, before.usage.modelTokens),
    computeCoreHoursPerWeek: before.limits?.computeCoreHoursPerWeek ?? config.quota.computeCoreHoursPerWeek,
    hostingSlots: before.limits?.hostingSlots ?? HOSTING_SLOTS,
  };
  await api.setWorkspaceQuota(state.workspaceId, limits);
  const after = await api.workspaceQuota(state.workspaceId);
  const admission = await api.quotaAdmission(config.project);
  record.note('limits', limits);
  record.note('status', after.status);
  record.note('message', after.message);
  record.note('admission', admission);
  expect(after.status === 'warning' || after.status === 'blocked', `The quota status is ${after.status}, not a warning.`);
  expect(after.codingAllowed === false && admission.allowed === false, 'New work is still admitted at the lowered quota.',
    { codingAllowed: after.codingAllowed, admitted: admission.allowed });
  await ctx.browser.openStudio(`/#/projects/${state.projectId}/board`);
  const banner = await ctx.browser.testIdState('workspace-quota-status');
  record.screenshot(await ctx.browser.screenshot('quota-warning'), 'Quota warning in the Studio header');
  record.note('banner', { visible: banner.visible, status: banner.status, text: banner.text });
  expect(banner.visible, 'The quota warning is not shown in Studio.');
}

// ----- teardown -----

async function stopEnvironments(ctx, record) {
  await ensureSignedIn(ctx);
  const { api, config } = ctx;
  const { own } = await ownedScope(ctx);
  if (!own.length) { record.note('stopRequests', []); return; }
  const stopped = await stopAll(ctx, config.project);
  record.note('stopRequests', stopped);
  if (!stopped.length) return;
  // The host stops an environment; it counts as running until the host acknowledges.
  await waitFor('the host to stop stage and production', async () => {
    const view = await api.environments(config.project).catch(() => null);
    return (view?.environments ?? []).every(item => !item.deployedArtifact || item.health === 'stopped');
  }, poll(ctx, config.deployTimeoutMs));
  record.note('stopped', stopped.map(item => item.environment));
}

async function removeProject(ctx, record) {
  await ensureSignedIn(ctx);
  const { api } = ctx;
  const { own } = await ownedScope(ctx);
  for (const project of own) await api.deleteProject(project.id);
  record.note('removed', own.map(project => project.id));
  const ids = new Set(own.map(project => project.id));
  const left = (await api.projects()).filter(project => ids.has(project.id));
  expect(left.length === 0, 'The project is still listed after removal.');
}

async function removeRepository(ctx) {
  // DELETE /api/projects/{id} keeps the repository; deletion exists only as the
  // compensation of a failed create (RegistryEndpoints.DeleteProvisionedRepositoryAsync).
  throw new StepSkipped('Studio has no route that deletes a provisioned repository; it stays in the test organisation. '
    + 'Each run uses its own repository name, so a rerun is not blocked.',
  { repository: ctx.state.repositoryUrl ?? 'not created in this run', proposedCard: 'Delete a template repository with its project' });
}

async function removeWorkspace(ctx, record) {
  await ensureSignedIn(ctx);
  const { api, config } = ctx;
  const { workspace } = await ownedScope(ctx);
  if (workspace) await api.deleteWorkspace(workspace.id);
  record.note('removed', workspace?.id ?? null);
  const left = (await api.workspaces()).some(ws => ws.displayName === config.workspace);
  expect(!left, 'The workspace is still listed after removal.');
}

// ----- shared -----

// Teardown removes only what lives in the scenario's own workspace. Project
// names are unique across Studio, so a same-named project in another workspace
// (the reason setup refused to run) belongs to someone else. A workspace that
// holds other projects is not the scenario's either; teardown leaves it whole.
async function ownedScope(ctx) {
  const { api, config } = ctx;
  const workspace = (await api.workspaces()).find(ws => ws.displayName === config.workspace);
  if (!workspace) return { workspace: null, own: [] };
  const foreign = (workspace.projects ?? []).filter(project => project.displayName !== config.project);
  if (foreign.length) {
    throw new StepSkipped(`Workspace "${config.workspace}" holds other projects; teardown leaves it untouched.`,
      { workspace: workspace.id, projects: foreign.map(project => project.displayName) });
  }
  return { workspace, own: (workspace.projects ?? []).filter(project => project.displayName === config.project) };
}

async function signIn(ctx) {
  const { credentials } = ctx;
  return ctx.api.signIn({ login: credentials.login, password: credentials.password,
    ...(credentials.totpSecret ? { totpCode: await freshTotp(ctx) } : {}) });
}

async function ensureSignedIn(ctx) {
  if (!ctx.api.session.csrfToken) await signIn(ctx);
}

/**
 * A time-based code that this run has not used yet: Studio refuses a replayed
 * code, and the API and the browser both sign in within one 30 s window.
 */
async function freshTotp(ctx) {
  const secret = ctx.credentials.totpSecret;
  if (!secret) return undefined;
  let window = Math.floor(ctx.clock.now().getTime() / 30_000);
  if (ctx.lastTotpWindow !== undefined && window <= ctx.lastTotpWindow) {
    await ctx.clock.sleep((ctx.lastTotpWindow + 1) * 30_000 - ctx.clock.now().getTime() + 500);
    window = Math.floor(ctx.clock.now().getTime() / 30_000);
  }
  ctx.lastTotpWindow = window;
  return totp(secret, ctx.clock.now().getTime());
}

async function createCard(ctx, id, title, promptMarkdown) {
  const { api, state } = ctx;
  const created = await api.createTask({
    id, title, project: state.projectId, promptMarkdown, targetState: READY, creationSource: 'lifecycle-scenario',
  });
  const card = await api.task(created.id ?? id, state.projectId);
  return { id: card.id ?? id, key: card.key, title };
}

/**
 * Takes cards to integration as the simulated user: queues a card left in the
 * backlog, accepts a card in Human Review only with green evidence (studio-op
 * OA-D5 policy), and waits for the rest.
 */
async function driveCards(ctx, record, cards, timeoutMs) {
  const { api, state } = ctx;
  const pending = new Map(cards.map(card => [card.id, card]));
  const accepted = [];
  await waitFor(`integration of ${cards.map(card => card.key ?? card.id).join(', ')}`, async () => {
    for (const [id, known] of pending) {
      const card = await api.task(id, state.projectId).catch(() => null);
      if (!card) continue;
      if (INTEGRATED.has(card.state)) { pending.delete(id); continue; }
      if (card.state === BACKLOG) { await api.moveTask(id, state.projectId, READY); continue; }
      if (card.state !== HUMAN_REVIEW) continue;
      const verdict = evaluateAcceptance(card, await api.reviewProjection(id, state.projectId));
      if (!verdict.acceptable)
        throw new StepFailure(`${card.key ?? id} stays in Human Review: ${verdict.reasons.map(reason => reason.code).join(', ')}.`,
          { card: known.key ?? id, reasons: verdict.reasons });
      await api.moveTask(id, state.projectId, '6-completed');
      accepted.push({ card: card.key ?? id, review: verdict.evidence.attemptId, buildTests: verdict.evidence.buildTests });
    }
    return pending.size === 0;
  }, poll(ctx, timeoutMs));
  record.note('accepted', [...(record.evidence.accepted ?? []), ...accepted]);
}

/**
 * Waits until the release that carries one of `cardKeys` runs healthy on stage
 * and waits for the production approval. Returns the release and its artifact.
 */
async function waitForStage(ctx, cardKeys) {
  const { api, config } = ctx;
  return waitFor(`stage of the release with ${cardKeys.join(', ')}`, async () => {
    const matching = (await api.releases(config.project))
      .filter(item => (item.cardKeys ?? []).some(key => cardKeys.includes(key)))
      .sort((a, b) => String(b.createdAtUtc).localeCompare(String(a.createdAtUtc)));
    // Each integration releases the cards added since the previous release, so
    // the newest release carries all of them once every key has been released.
    const released = new Set(matching.flatMap(item => item.cardKeys));
    const release = cardKeys.every(key => released.has(key)) ? matching[0] : null;
    if (!release) return null;
    const view = await api.environments(config.project);
    const promotion = [...view.promotions].reverse().find(item => (release.artifactDigests ?? []).includes(item.artifact));
    if (!promotion) return null;
    if (promotion.status === 'failed')
      throw new StepFailure(`Stage failed for release ${release.version}.`, { release: release.id, artifact: promotion.artifact });
    const stage = environment(view, 'stage');
    if (promotion.status !== 'pending' || stage?.deployedArtifact !== promotion.artifact || stage.health !== 'healthy') return null;
    return { release, artifact: promotion.artifact, promotion, stage };
  }, poll(ctx, config.releaseTimeoutMs));
}

/** Approves production for `artifact` and waits until it runs healthy there. */
async function promote(ctx, record, artifact) {
  const { api, config } = ctx;
  await api.approvePromotion(config.project, artifact);
  record.note('approved', artifact);
  return waitFor(`production health of ${artifact}`, async () => {
    const view = await api.environments(config.project);
    const production = environment(view, 'production');
    const promotion = [...view.promotions].reverse().find(item => item.artifact === artifact);
    if (promotion?.status === 'failed' || production?.history.some(item => item.artifact === artifact && item.status === 'failed'))
      throw new StepFailure(`Production failed for ${artifact}.`, { history: production?.history.slice(0, 3) });
    return production?.deployedArtifact === artifact && production.health === 'healthy' ? production : null;
  }, poll(ctx, config.deployTimeoutMs));
}

async function stopAll(ctx, projectName) {
  const view = await ctx.api.environments(projectName).catch(() => null);
  const stopped = [];
  for (const item of view?.environments ?? []) {
    if (!item.deployedArtifact || item.health === 'stopped') continue;
    const answer = await ctx.api.stopEnvironment(projectName, item.declaration.id);
    stopped.push({ environment: item.declaration.id, status: answer.status });
  }
  return stopped;
}

async function shoot(ctx, record, route, name, caption) {
  await ctx.browser.openStudio(route);
  record.screenshot(await ctx.browser.screenshot(name), caption);
}

function noteRelease(record, staged) {
  record.note('release', staged.release.version);
  record.note('commit', staged.release.commitSha);
  record.note('artifact', staged.artifact);
  record.note('stageHealth', staged.stage.health);
}

function environment(view, kind) {
  return view?.environments?.find(item => item.declaration?.kind === kind) ?? null;
}

function keys(cards) {
  return cards.map(card => card.key).filter(Boolean);
}

function poll(ctx, timeoutMs) {
  return { clock: ctx.clock, timeoutMs, intervalMs: ctx.config.pollMs };
}

function excerpt(text) {
  return typeof text === 'string' ? text.replace(/\s+/g, ' ').trim().slice(0, 300) : null;
}

// ----- card prompts -----

/**
 * Stage checks the page; production also checks /health.txt. Stage and
 * production run the same digest, so this stricter production contract is
 * what lets a seeded change pass stage and fail production health.
 */
export function environmentDeclarations(config) {
  const declaration = (kind, target) => ({
    id: kind,
    name: kind === 'stage' ? 'Stage' : 'Production',
    kind,
    targetHostId: target.host,
    address: target.address,
    template: 'static',
    destination: target.directory,
    healthUrl: kind === 'stage' ? `${target.address}/` : `${target.address}/health.txt`,
    ...(kind === 'production' ? { approval: 'person' } : {}),
    healthChecksAllowPrivateNetwork: true,
  });
  return { stage: declaration('stage', config.stage), production: declaration('production', config.production) };
}

function environmentsPrompt(config) {
  const { stage, production } = environmentDeclarations(config);
  return [
    '# Declare stage and production',
    '',
    'The lifecycle scenario (AGT-3178) creates this card. Make exactly these changes and nothing else.',
    '',
    '1. Add `docs/deployments/environments/stage.json` with this content:',
    '',
    '```json',
    JSON.stringify(stage, null, 2),
    '```',
    '',
    '2. Add `docs/deployments/environments/production.json` with this content:',
    '',
    '```json',
    JSON.stringify(production, null, 2),
    '```',
    '',
    '3. Add `health.txt` at the repository root with the single line `ok`, and copy it to `dist/health.txt` in `build.mjs` next to `index.html`.',
    '',
    'Keep `npm test` and `npm run build` green.',
  ].join('\n');
}

function faultPrompt() {
  return [
    '# Seeded fault: remove the production health file',
    '',
    'The lifecycle scenario (AGT-3178) seeds this change on purpose. It proves that a failing production health check',
    'rolls production back and opens a card. Delete `health.txt` and remove the line in `build.mjs` that copies it to',
    '`dist/`. Change nothing else; `npm test` and `npm run build` stay green.',
  ].join('\n');
}

function fixPrompt(failure) {
  return [
    '# Restore the production health file',
    '',
    `Production failed its health check (${failure.key}: ${failure.title}): \`/health.txt\` is missing from the build.`,
    'Add `health.txt` at the repository root with the single line `ok` again, and copy it to `dist/health.txt` in',
    '`build.mjs` next to `index.html`. Add a test in `app.test.mjs` that `npm run build` writes `dist/health.txt`.',
    'Keep `npm test` and `npm run build` green.',
  ].join('\n');
}
