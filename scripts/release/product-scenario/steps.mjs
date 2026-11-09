// The steps of the product scenario (Dossier application-lifecycle, card
// AGT-3183): a product manager, a designer and a manager evolve "Click
// Counter", which the lifecycle scenario (AGT-3178) left on stage and
// production. Each step acts as one person through the browser or the API,
// records what it saw, and throws a StepFailure with the evidence when Studio
// does not do what the step expects.
import { randomBytes } from 'node:crypto';

import { PEOPLE, PRODUCT_ROLE, personLogin } from './config.mjs';
import { StepFailure, StepSkipped, expect, waitFor } from './runner.mjs';
import { SessionClient, totp } from './session.mjs';
import { ProductApi, REFUSED_READS } from './studio-api.mjs';

const BACKLOG = '0-backlog';
const READY = '2-ready';
const HUMAN_REVIEW = '5-human-review';
const COMPLETED = '6-completed';
const ARCHIVE = '7-archive';
const DONE = new Set([COMPLETED, ARCHIVE]);
const FEEDBACK_HEADING = "Preview feedback for this card's next fix round:";
const PREVIEW_GONE = new Set(['removed', 'expired']);
// Teardown finds the Project URL by this label, also after a run without teardown.
const PROJECT_URL_LABEL = 'Stage (product scenario)';
// The largest viewport the scenario opens to measure the page behind a screenshot.
const MAX_VIEWPORT = { width: 2560, height: 4000 };
const MARK_MARGIN = 0.01;
const RESET_BUTTON = '^reset$|reset';

export function setupSteps() {
  return [
    { id: 'sign-in-owner', phase: 'setup', actor: 'Owner', title: 'Sign in the Owner through the API', run: signInOwner },
    { id: 'project', phase: 'setup', actor: 'Owner', title: 'Find Click Counter live on stage and production', needs: ['sign-in-owner'], run: findProject },
    { id: 'leftovers', phase: 'setup', actor: 'Owner', title: 'Disable people and archive cards an earlier run left', needs: ['project'], run: removeLeftovers },
  ];
}

export function scenarioSteps() {
  return [
    { id: 'invite', actor: 'Owner', title: '1. The Owner invites three people with the Product role', needs: ['leftovers'], run: invitePeople },
    { id: 'wish', actor: 'Product manager', title: '2. The product manager asks for the change in the product chat', needs: ['invite'], run: writeWish },
    { id: 'delivery', actor: 'Product manager', title: '3. Coding, review and gates run; the card preview shows before and after', needs: ['wish'], run: awaitDelivery },
    { id: 'feedback', actor: 'Designer', title: '4. The designer marks the reset button; the fix round updates the preview', needs: ['delivery'], run: sendFeedback },
    { id: 'accept', actor: 'Product manager', title: '5a. The product manager accepts the preview', needs: ['feedback'], run: acceptPreview },
    { id: 'release', actor: 'Manager', title: '5b. The manager releases it; it goes to stage and production', needs: ['accept'], run: releaseToProduction },
    { id: 'refusals', actor: 'Designer', title: '6. A contributor tries credentials and host settings and is refused', needs: ['invite'], run: tryRefusedActions },
  ];
}

export function teardownSteps() {
  return [
    { id: 'retire-card', phase: 'teardown', always: true, title: 'Archive an unfinished scenario card and wait for its preview to go', run: retireCard },
    { id: 'disable-people', phase: 'teardown', always: true, title: 'Disable the invited people and remove their bindings', run: disablePeople },
    { id: 'remove-project-url', phase: 'teardown', always: true, title: 'Remove the Project URL the scenario added', run: removeProjectUrl },
    { id: 'delete-people', phase: 'teardown', always: true, title: 'Delete the invited people', run: deletePeople },
  ];
}

// ----- setup -----

async function signInOwner(ctx, record) {
  const user = await ownerSignIn(ctx);
  record.note('login', ctx.credentials.login);
  record.note('role', user?.role ?? 'unknown');
  expect(user?.role === 'owner', 'The scenario signs in as the Owner: invitations and production approval are Owner actions.',
    { role: user?.role });
}

async function findProject(ctx, record) {
  const { owner, config, state } = ctx;
  const project = (await owner.projects()).find(item => item.displayName === config.project);
  expect(project, `There is no project "${config.project}". Run the lifecycle scenario (AGT-3178) with --teardown never first.`);
  state.project = { id: project.id, name: project.displayName, shortCode: project.shortCode ?? null };
  const workspace = (await owner.workspaces()).find(ws => (ws.projects ?? []).some(item => item.id === project.id));
  state.workspaceId = workspace?.id ?? project.workspaceId ?? null;
  record.note('projectId', project.id);
  record.note('workspaceId', state.workspaceId);

  const view = await owner.environments(project.displayName).catch(() => null);
  const stage = environment(view, 'stage');
  const production = environment(view, 'production');
  expect(stage && production, 'The project does not declare stage and production.', { environments: (view?.environments ?? []).map(item => item.declaration?.id) });
  expect(production.deployedArtifact && production.health === 'healthy', 'Production does not run healthy; the scenario evolves a live website.',
    { production: { artifact: production.deployedArtifact, health: production.health } });
  state.addresses = { stage: stage.declaration.address, production: production.declaration.address };
  state.productionBefore = production.deployedArtifact;
  record.note('stage', state.addresses.stage);
  record.note('production', state.addresses.production);
  record.note('productionArtifact', production.deployedArtifact);

  // The product controls sit beside a Project URL; the stage address is the live website.
  const existing = (project.urls ?? []).find(url => sameAddress(url.url, state.addresses.stage));
  if (existing) {
    state.projectUrlId = existing.id;
    record.note('projectUrl', `${existing.id} "${existing.label}" (existing)`);
  } else {
    const updated = await owner.addProjectUrl(project.id, PROJECT_URL_LABEL, state.addresses.stage);
    const added = (updated?.urls ?? []).find(url => sameAddress(url.url, state.addresses.stage));
    expect(added, 'Studio did not list the Project URL it was asked to add.');
    state.projectUrlId = added.id;
    record.note('projectUrl', `${added.id} "${PROJECT_URL_LABEL}" (added)`);
  }
  state.initialCardIds = (await owner.tasks(project.id)).map(card => card.id);
  record.note('cardsBefore', state.initialCardIds.length);
}

async function removeLeftovers(ctx, record) {
  const { owner, config, state } = ctx;
  const leftovers = (await owner.users()).filter(user => isScenarioLogin(config, user.username) && !user.disabled);
  for (const user of leftovers) await retireUser(ctx, user);
  record.note('disabledUsers', leftovers.map(user => user.username));
  const title = cardTitle(config.wish);
  const cards = (await owner.tasks(state.project.id))
    .filter(card => card.title === title && !DONE.has(card.state));
  for (const card of cards) await owner.moveTask(card.id, state.project.id, ARCHIVE);
  record.note('archivedCards', cards.map(card => card.key ?? card.id));
  if (cards.length) state.initialCardIds = (await owner.tasks(state.project.id)).map(card => card.id);
}

// ----- scenario -----

async function invitePeople(ctx, record) {
  const { owner, config, state } = ctx;
  const invited = [];
  for (const person of PEOPLE) {
    const login = personLogin(config, person, ctx.runId);
    const answer = await owner.invite({ username: login, displayName: person.displayName, role: PRODUCT_ROLE, projects: [state.project.id] });
    expect(answer?.signInLink && answer?.user?.id, `Studio returned no one-time sign-in link for ${login}.`);
    ctx.secrets.push(answer.signInLink, inviteToken(answer.signInLink));
    // The run chooses each password; it never leaves memory and is scrubbed from the report.
    const password = randomBytes(18).toString('base64url');
    ctx.secrets.push(password);
    const page = await ctx.browser.page(person.id);
    await page.redeemInvitation(answer.signInLink, password);
    const gate = await page.testIdState('auth-gate', 2_000);
    record.screenshot(await page.screenshot('joined'), `${person.title} after the one-time sign-in link`);
    expect(!gate.visible, `${person.title} still sees the sign-in gate after accepting the invitation.`);
    const api = new ProductApi(new SessionClient({ url: config.studioUrl, clientId: `product-scenario-${person.id}`, fetchImpl: ctx.fetchImpl }));
    const user = await api.signIn({ login, password });
    ctx.people.set(person.id, { person, login, userId: answer.user.id, api, page, user });
    invited.push({ person: person.id, login, userId: answer.user.id, expiresAt: answer.expiresAt });
  }
  record.note('invited', invited);

  const manager = ctx.people.get('manager');
  await owner.updateUser(manager.userId, { productionApprovalProjects: [state.project.id] });
  const users = await owner.users();
  const rows = PEOPLE.map(person => {
    const user = users.find(item => item.id === ctx.people.get(person.id).userId);
    return { person: person.id, role: user?.role, projects: user?.projects, productionApprovalProjects: user?.productionApprovalProjects ?? [] };
  });
  record.note('users', rows);
  for (const row of rows) {
    expect(row.role === PRODUCT_ROLE && (row.projects ?? []).includes(state.project.id),
      `${row.person} does not hold the Product role on ${state.project.id}.`, { user: row });
    const approval = (row.productionApprovalProjects ?? []).includes(state.project.id);
    expect(approval === PEOPLE.find(person => person.id === row.person).productionApproval,
      `${row.person} ${approval ? 'holds' : 'lacks'} the production approval, against the scenario.`, { user: row });
  }
}

async function writeWish(ctx, record) {
  const { config, owner, state } = ctx;
  const pm = ctx.people.get('pm');
  const page = pm.page;
  await page.openProjectUrl(state.project.name, state.projectUrlId);
  record.screenshot(await page.screenshot('preview'), 'Live stage beside the product controls');

  // The chat beside the preview answers in product language (observedSurface "URL preview").
  const chat = await pm.api.productChat(state.project.id, config.wish);
  record.note('productChat', { status: chat.status, reply: excerpt(chat.body?.reply?.text ?? chat.body?.error ?? chat.body?.message) });

  await page.click('product-start-change');
  await page.fill('product-wish', config.wish);
  await page.clickButtonIn('product-draft', 'Continue');
  const first = await page.testIdState('product-draft');
  record.screenshot(await page.screenshot('question-1'), 'First question of the product chat');
  await page.fill('product-location', config.location);
  await page.clickButtonIn('product-draft', 'Continue');
  const second = await page.testIdState('product-draft');
  record.screenshot(await page.screenshot('question-2'), 'Second question of the product chat');
  await page.fill('product-example', config.example);
  await page.clickButtonIn('product-draft', 'Review change');
  const proposal = await page.testIdState('product-proposal');
  record.screenshot(await page.screenshot('proposal'), 'Proposal with the quota estimate');
  const questions = [first.text, second.text].map(text => text?.match(/[^.?!]*\?/)?.[0]?.trim() ?? text);
  record.note('questions', questions);
  record.note('answers', [config.location, config.example]);
  record.note('proposal', proposal.text);
  expect(questions.every(text => text?.endsWith('?')), 'The product chat did not ask two questions.', { questions });
  expect(proposal.visible && /token/i.test(proposal.text ?? ''), 'The proposal shows no quota estimate.', { proposal: proposal.text });

  const title = cardTitle(config.wish);
  const early = (await owner.tasks(state.project.id)).filter(card => !state.initialCardIds.includes(card.id));
  expect(early.length === 0, 'A card exists before the product manager confirmed the proposal.', { cards: early.map(card => card.key) });

  const created = await page.captureRequest('POST', '/api/tasks', () => page.click('product-confirm-change'));
  record.note('createStatus', created.status);
  expect(created.status >= 200 && created.status < 300, `Studio answered ${created.status} to the confirmed change.`);
  const card = await waitFor('the confirmed card', async () =>
    (await owner.tasks(state.project.id)).find(item => !state.initialCardIds.includes(item.id) && item.title === title) ?? null,
  poll(ctx, 2 * 60_000));
  state.card = { id: card.id, key: card.key, title: card.title };
  record.note('card', `${card.key}: ${card.title}`);
  record.note('lane', card.state);
  record.screenshot(await page.screenshot('change-created'), 'Product controls after the change was created');
}

async function awaitDelivery(ctx, record) {
  const { owner, config, state } = ctx;
  const pm = ctx.people.get('pm');
  // A Product card waits in Ready; move one left in the backlog, then let coding, review and gates run.
  const reviewed = await waitFor(`Human Review of ${state.card.key}`, async () => {
    const card = await owner.task(state.card.id, state.project.id);
    if (card.state === BACKLOG) await owner.moveTask(card.id, state.project.id, READY);
    if (DONE.has(card.state)) throw new StepFailure(`${state.card.key} left the board before anyone looked at it (${card.state}).`);
    return card.state === HUMAN_REVIEW ? card : null;
  }, poll(ctx, config.codingTimeoutMs));
  record.note('lane', reviewed.state);
  const preview = await awaitPreview(ctx, record, null);
  state.preview = preview;
  record.note('preview', preview);

  const shots = await beforeAfter(ctx);
  state.screenshotUrls = shots.all.map(shot => shot.url);
  record.note('screenshots', shots.all.map(shot => shot.caption));
  expect(shots.before && shots.after, 'The delivery has no before and after screenshots.', { captions: shots.all.map(shot => shot.caption) });

  const page = pm.page;
  await page.openProjectUrl(state.project.name, state.projectUrlId);
  await page.select('product-card-select', state.card.id);
  const status = await page.testIdState('product-status');
  const comparison = await page.testIdState('product-before-after');
  record.note('plainStatus', status.text);
  record.screenshot(await page.screenshot('before-after'), 'Before and after of the delivery in the product controls');
  expect(comparison.visible, 'The product controls show no before and after images.');
  const opened = await page.openAddress(preview.address);
  record.note('previewHttpStatus', opened.status);
  record.screenshot(await page.screenshot('card-preview'), `Card preview ${preview.address}`);
  expect(opened.status === 200, `The card preview answered ${opened.status}.`);
}

async function sendFeedback(ctx, record) {
  const { owner, config, state } = ctx;
  const designer = ctx.people.get('designer');
  const page = designer.page;
  await page.openProjectUrl(state.project.name, state.projectUrlId);
  await page.select('product-card-select', state.card.id);
  const shots = await beforeAfter(ctx);
  if (shots.after) await page.clickText(shots.after.caption).catch(() => undefined);
  const image = await page.imageSize('product-markup-image');
  record.note('markedImage', image.src);

  // Locate the reset button on the preview at the size of the screenshot, then mark the same area.
  const viewport = { width: clamp(image.width, 320, MAX_VIEWPORT.width), height: clamp(image.height, 240, MAX_VIEWPORT.height) };
  const before = await page.measureButton(state.preview.address, RESET_BUTTON, viewport);
  record.note('resetButtonBefore', summary(before));
  expect(before.found, 'The card preview has no reset button to mark.', { buttons: before.buttons });
  const box = widen(before.box);
  await page.openProjectUrl(state.project.name, state.projectUrlId);
  await page.select('product-card-select', state.card.id);
  if (shots.after) await page.clickText(shots.after.caption).catch(() => undefined);
  await page.markImage('product-markup-image', box);
  const selection = await page.testIdState('product-selection', 5_000);
  expect(selection.visible, 'The marked area is not shown on the screenshot.');
  await page.fill('product-comment', config.feedback);
  await page.clickText('Element details, if known');
  await page.fill('product-selector', before.selector);
  record.screenshot(await page.screenshot('marked'), 'The designer marks the reset button and writes the feedback');

  const sent = await page.captureRequest('POST', '/preview-feedback', () => page.click('product-send-feedback'));
  // Chromium may hide a multipart body with a file; then the report shows what the designer entered.
  const fields = multipartFields(sent.body);
  const captured = Boolean(fields.comment);
  record.note('feedbackStatus', sent.status);
  record.note('sent', captured
    ? { comment: fields.comment, selector: fields.selector, previewAddress: fields.previewAddress, area: box, bodyCaptured: true }
    : { comment: config.feedback, selector: before.selector, area: box, bodyCaptured: false });
  if (sent.status === 401 || sent.status === 403) {
    const code = jsonError(sent.answer);
    ctx.refusals.push({ actor: 'Designer', action: 'Send feedback on the preview', request: 'POST /api/tasks/{id}/preview-feedback',
      status: sent.status, code, refused: true, expected: false });
    record.screenshot(await page.screenshot('feedback-refused'), 'Studio refused the feedback');
    throw new StepFailure(`Studio refused the designer's feedback (${[sent.status, code].filter(Boolean).join(' ')}); `
      + 'the Product role cannot send preview feedback.', { status: sent.status, code });
  }
  expect(sent.status >= 200 && sent.status < 300, `Studio answered ${sent.status} to the feedback.`);
  record.screenshot(await page.screenshot('feedback-sent'), 'Feedback sent');

  // The fix round reads the card prompt: the feedback must be there, with the crop, selector and address.
  const prompt = await waitFor('the feedback in the card prompt', async () => {
    const answer = await owner.promptFile(state.card.id, state.project.id);
    const text = answer.status === 200 ? answer.body : '';
    const index = text.lastIndexOf(FEEDBACK_HEADING);
    return index >= 0 && text.slice(index).includes(config.feedback) ? text.slice(index) : null;
  }, poll(ctx, 5 * 60_000));
  const section = prompt.split(/\n---\n/)[0];
  state.feedback = {
    by: designer.login,
    comment: config.feedback,
    selector: lineValue(section, 'Element selector'),
    previewAddress: lineValue(section, 'Preview address'),
    crop: lineValue(section, 'Screenshot crop'),
    area: box,
    fixRoundPrompt: section.trim(),
  };
  record.note('fixRoundPrompt', section.trim());
  expect(state.feedback.selector === before.selector, 'The fix round prompt does not name the marked element.', { selector: state.feedback.selector });
  expect(state.feedback.crop, 'The fix round prompt does not carry the screenshot crop.');

  const updated = await waitFor(`the fix round of ${state.card.key}`, async () => {
    const card = await owner.task(state.card.id, state.project.id);
    return card.state === HUMAN_REVIEW && card.preview?.status === 'ready' && card.preview.deliverySha !== state.preview.deliverySha
      ? card : null;
  }, poll(ctx, config.fixTimeoutMs));
  state.feedback.fixDelivery = updated.preview.deliverySha;
  record.note('previewBefore', state.preview.deliverySha);
  record.note('previewAfter', updated.preview.deliverySha);
  state.preview = previewOf(updated.preview);

  const after = await page.measureButton(state.preview.address, RESET_BUTTON, viewport);
  record.note('resetButtonAfter', summary(after));
  const smaller = after.found && area(after.pixels) < area(before.pixels);
  state.feedback.resetButton = { before: summary(before), after: summary(after), smaller };
  await page.openProjectUrl(state.project.name, state.projectUrlId);
  await page.select('product-card-select', state.card.id);
  await page.click('product-check-update').catch(() => undefined);
  await page.testIdState('product-accept', 15_000);
  record.screenshot(await page.screenshot('updated-preview'), 'Product controls after the fix round');
  await page.openAddress(state.preview.address);
  record.screenshot(await page.screenshot('card-preview-fixed'), `Updated card preview ${state.preview.address}`);
  expect(after.found, 'The updated preview has no reset button.', { buttons: after.buttons });
  expect(smaller, 'The reset button is not smaller after the fix round.', { before: before.pixels, after: after.pixels });
}

async function acceptPreview(ctx, record) {
  const { owner, state } = ctx;
  const pm = ctx.people.get('pm');
  const page = pm.page;
  await page.openProjectUrl(state.project.name, state.projectUrlId);
  await page.select('product-card-select', state.card.id);
  const button = await page.testIdState('product-accept');
  record.screenshot(await page.screenshot('ready-to-accept'), 'The updated preview, ready to accept');
  expect(button.visible, 'The product manager is offered no accept button.');
  const accepted = await page.captureRequest('POST', '/move', () => page.click('product-accept'));
  record.note('moveStatus', accepted.status);
  expect(accepted.status >= 200 && accepted.status < 300, `Studio answered ${accepted.status} to the acceptance.`);
  const card = await waitFor(`${state.card.key} accepted`, async () => {
    const current = await owner.task(state.card.id, state.project.id);
    return DONE.has(current.state) ? current : null;
  }, poll(ctx, 2 * 60_000));
  record.note('lane', card.state);
  const status = await page.testIdState('product-status');
  record.note('plainStatus', status.text);
  record.screenshot(await page.screenshot('accepted'), 'Accepted; a release approver decides when it goes live');
}

async function releaseToProduction(ctx, record) {
  const { config, state } = ctx;
  const manager = ctx.people.get('manager');
  const pm = ctx.people.get('pm');
  const staged = await waitForStage(ctx, state.card.key);
  record.note('release', staged.release.version);
  record.note('commit', staged.release.commitSha);
  record.note('artifact', staged.artifact);
  record.note('stageHealth', staged.stage.health);
  const stagePage = await manager.page.openAddress(state.addresses.stage);
  record.note('stageHttpStatus', stagePage.status);
  record.screenshot(await manager.page.screenshot('stage'), 'The change on stage, waiting for the release');

  // Only the manager holds the production approval; the product manager is refused.
  const denied = await pm.api.approvePromotion(state.project.name, staged.artifact);
  ctx.refusals.push({ actor: 'Product manager', action: 'Release to production without the approval',
    request: `POST /api/projects/${state.project.name}/environments/promotions/approve`,
    status: denied.status, code: denied.body?.error ?? null, refused: denied.status === 403, expected: true });
  record.note('productManagerApproval', { status: denied.status, error: denied.body?.error ?? null });
  expect(denied.status === 403, `The product manager's release answered ${denied.status}, not 403.`);

  const approved = await manager.api.approvePromotion(state.project.name, staged.artifact);
  record.note('managerApproval', { status: approved.status, error: approved.body?.error ?? null });
  expect(approved.status >= 200 && approved.status < 300, `The manager's release answered ${approved.status}.`, { body: approved.body });
  const production = await waitFor(`production health of ${staged.artifact}`, async () => {
    const view = await ctx.owner.environments(state.project.name);
    const live = environment(view, 'production');
    if (live?.history?.some(item => item.artifact === staged.artifact && item.status === 'failed'))
      throw new StepFailure(`Production failed for ${staged.artifact}.`, { history: live.history.slice(0, 3) });
    return live?.deployedArtifact === staged.artifact && live.health === 'healthy' ? live : null;
  }, poll(ctx, config.deployTimeoutMs));
  record.note('productionArtifact', production.deployedArtifact);
  record.note('sameDigestAsStage', production.deployedArtifact === staged.artifact);
  const live = await manager.page.measureButton(state.addresses.production, RESET_BUTTON);
  record.note('productionHttpStatus', live.status);
  record.note('productionResetButton', summary(live));
  record.screenshot(await manager.page.screenshot('production'), 'The change live on production');
  expect(live.status === 200, `The production address answered ${live.status}.`);
}

async function tryRefusedActions(ctx, record) {
  const designer = ctx.people.get('designer');
  const rows = [];
  for (const person of ctx.people.values()) {
    for (const read of REFUSED_READS) {
      const answer = await person.api.tryRead(read.path);
      const row = { actor: person.person.title, action: read.action, request: `GET ${read.path}`, status: answer.status,
        code: answer.body?.error ?? null, refused: answer.status === 403, expected: true };
      rows.push(row);
      ctx.refusals.push(row);
    }
  }
  record.note('tried', rows.length);
  record.note('answers', rows.map(row => `${row.actor}: ${row.request} ${row.status} ${row.code ?? ''}`.trim()));
  await designer.page.openStudio('/#/workspace/settings/credentials');
  record.note('credentialsPage', excerpt(await designer.page.bodyText()));
  record.screenshot(await designer.page.screenshot('credentials'), 'The designer opens the credentials');
  await designer.page.openStudio('/#/workspace/settings/environment');
  record.note('hostsPage', excerpt(await designer.page.bodyText()));
  record.screenshot(await designer.page.screenshot('hosts'), 'The designer opens the host settings');
  const reached = rows.filter(row => !row.refused);
  expect(reached.length === 0, `A Product person reached ${reached.map(row => row.request).join(', ')}.`, { reached });
}

// ----- teardown -----

async function retireCard(ctx, record) {
  await ensureOwner(ctx);
  const { owner, config } = ctx;
  const project = await scenarioProject(ctx);
  if (!project) { record.note('cards', 'no project'); return; }
  // This run's card, or after --teardown never an open card with the scenario title.
  const title = cardTitle(config.wish);
  const cards = ctx.state.card
    ? [await owner.task(ctx.state.card.id, project.id).catch(() => null)].filter(Boolean)
    : (await owner.tasks(project.id)).filter(card => card.title === title && !DONE.has(card.state));
  const archived = [];
  for (const card of cards) {
    // An accepted card keeps its place; its preview goes after integration. An unfinished one is archived.
    if (DONE.has(card.state)) continue;
    await owner.moveTask(card.id, project.id, ARCHIVE);
    archived.push(card.key ?? card.id);
  }
  record.note('archived', archived);
  const previews = {};
  for (const card of cards.filter(item => item.preview)) {
    const gone = await waitFor(`removal of the ${card.key} preview`, async () => {
      const current = await owner.task(card.id, project.id).catch(() => null);
      return !current?.preview || PREVIEW_GONE.has(current.preview.status) ? current?.preview ?? { status: 'none' } : null;
    }, poll(ctx, config.deployTimeoutMs));
    previews[card.key ?? card.id] = gone.status;
  }
  record.note('previews', previews);
}

async function disablePeople(ctx, record) {
  await ensureOwner(ctx);
  const { owner, config } = ctx;
  const users = (await owner.users()).filter(user => isScenarioLogin(config, user.username) && !user.disabled);
  for (const user of users) await retireUser(ctx, user);
  record.note('disabled', users.map(user => user.username));
  const left = (await owner.users()).filter(user => isScenarioLogin(config, user.username) && !user.disabled);
  expect(left.length === 0, 'An invited person can still sign in.', { left: left.map(user => user.username) });
}

async function removeProjectUrl(ctx, record) {
  await ensureOwner(ctx);
  const project = await scenarioProject(ctx);
  const added = (project?.urls ?? []).filter(url => url.label === PROJECT_URL_LABEL);
  for (const url of added) await ctx.owner.removeProjectUrl(project.id, url.id);
  record.note('removed', added.map(url => url.id));
}

async function deletePeople(ctx) {
  // AccessSecurityEndpoints has no delete-user route; a disabled user keeps its login.
  throw new StepSkipped('Studio has no route that deletes a user; the people stay disabled. Each run invites new logins, so a rerun is not blocked.',
    { logins: [...ctx.people.values()].map(person => person.login), proposedCard: 'Delete an invited user and revoke an open invitation' });
}

// ----- shared -----

/** The scenario project as Studio lists it now, also in a teardown-only run. */
async function scenarioProject(ctx) {
  return (await ctx.owner.projects()).find(item => item.displayName === ctx.config.project) ?? null;
}

async function ownerSignIn(ctx) {
  const { credentials } = ctx;
  return ctx.owner.signIn({ login: credentials.login, password: credentials.password,
    ...(credentials.totpSecret ? { totpCode: totp(credentials.totpSecret, ctx.clock.now().getTime()) } : {}) });
}

async function ensureOwner(ctx) {
  if (!ctx.owner.session.signedIn) await ownerSignIn(ctx);
}

/** Clears a scenario user's production approval, disables it and removes its role bindings. */
async function retireUser(ctx, user) {
  const { owner } = ctx;
  if ((user.productionApprovalProjects ?? []).length) await owner.updateUser(user.id, { productionApprovalProjects: [] });
  await owner.updateUser(user.id, { disabled: true });
  const iam = await owner.iam().catch(() => null);
  for (const binding of iam?.bindings ?? [])
    if (binding.principalKind === 'user' && binding.principalId === user.id) await owner.deleteBinding(binding.id);
}

/**
 * Waits until the card preview runs `ready` with a screenshot, for a delivery
 * other than `previousSha`. A preview that stays failed fails the step with
 * the job evidence instead of waiting for the whole coding timeout.
 */
async function awaitPreview(ctx, record, previousSha) {
  const { owner, config, state } = ctx;
  let lastSeen = null;
  try {
    const card = await waitFor(`the ${state.card.key} preview`, async () => {
      const current = await owner.task(state.card.id, state.project.id);
      lastSeen = current.preview ?? null;
      return current.preview?.status === 'ready' && current.preview.screenshotUrl && current.preview.deliverySha !== previousSha
        ? current : null;
    }, poll(ctx, config.deployTimeoutMs));
    return previewOf(card.preview);
  } catch (error) {
    const view = await owner.environments(state.project.name).catch(() => null);
    const jobs = (view?.jobs ?? []).filter(job => String(job.environmentId ?? '').startsWith('preview-')).slice(-3)
      .map(job => ({ kind: job.kind, status: job.status, cardKey: job.cardKey, error: job.error ?? job.evidence ?? null }));
    record.note('previewJobs', jobs);
    throw new StepFailure(`The card preview did not become ready (last status ${lastSeen?.status ?? 'none'}).`,
      { preview: lastSeen, reason: error.message });
  }
}

/** The before and after screenshots of the card (captions matched as the product controls match them). */
async function beforeAfter(ctx) {
  const { owner, state } = ctx;
  const answer = await owner.screenshots(state.card.id, state.project.id);
  const all = answer?.screenshots ?? [];
  return {
    all,
    before: [...all].reverse().find(shot => /\bbefore\b/i.test(shot.caption)) ?? null,
    after: [...all].reverse().find(shot => /\bafter\b/i.test(shot.caption)) ?? null,
  };
}

/** Waits until the release that carries `cardKey` runs healthy on stage and waits for the production approval. */
async function waitForStage(ctx, cardKey) {
  const { owner, config, state } = ctx;
  return waitFor(`stage of the release with ${cardKey}`, async () => {
    const release = (await owner.releases(state.project.name))
      .filter(item => (item.cardKeys ?? []).includes(cardKey))
      .sort((a, b) => String(b.createdAtUtc).localeCompare(String(a.createdAtUtc)))[0];
    if (!release) return null;
    const view = await owner.environments(state.project.name);
    const promotion = [...view.promotions].reverse().find(item => (release.artifactDigests ?? []).includes(item.artifact));
    if (!promotion) return null;
    if (promotion.status === 'failed')
      throw new StepFailure(`Stage failed for release ${release.version}.`, { release: release.id, artifact: promotion.artifact });
    const stage = environment(view, 'stage');
    if (promotion.status !== 'pending' || stage?.deployedArtifact !== promotion.artifact || stage.health !== 'healthy') return null;
    return { release, artifact: promotion.artifact, promotion, stage };
  }, poll(ctx, config.releaseTimeoutMs));
}

function previewOf(preview) {
  return { address: preview.address, status: preview.status, deliverySha: preview.deliverySha,
    screenshotUrl: preview.screenshotUrl, expiresAtUtc: preview.expiresAtUtc };
}

/** Reads the text fields of a multipart body; the crop part is binary and skipped. */
export function multipartFields(body) {
  const fields = {};
  for (const part of String(body).split(/\r?\n--/)) {
    const name = part.match(/name="([^"]+)"(?!; filename=)/)?.[1];
    if (!name || /filename=/.test(part.split(/\r?\n\r?\n/)[0])) continue;
    fields[name] = part.split(/\r?\n\r?\n/).slice(1).join('\n\n').replace(/\r?\n$/, '').replace(/\r?\n--$/, '').trim();
  }
  return fields;
}

function jsonError(text) {
  try { return JSON.parse(text)?.error ?? null; } catch { return null; }
}

function lineValue(text, label) {
  return text.match(new RegExp(`^${label}: (.+)$`, 'm'))?.[1]?.trim() ?? null;
}

/** The marked area: the button box with a small margin, kept inside the image. */
export function widen(box, margin = MARK_MARGIN) {
  const x = Math.max(0, box.x - margin);
  const y = Math.max(0, box.y - margin);
  return {
    x: round4(x), y: round4(y),
    width: round4(Math.min(1 - x, box.width + 2 * margin)),
    height: round4(Math.min(1 - y, box.height + 2 * margin)),
  };
}

function summary(measured) {
  if (!measured?.found) return { found: false, status: measured?.status ?? null, buttons: measured?.buttons ?? [] };
  return { name: measured.name, selector: measured.selector, pixels: measured.pixels, className: measured.className, style: measured.style };
}

function area(pixels) { return (pixels?.width ?? 0) * (pixels?.height ?? 0); }
function clamp(value, low, high) { return Math.max(low, Math.min(high, Math.round(value))); }
function round4(value) { return Math.round(value * 10000) / 10000; }

/** The card title the product controls derive from the wish. */
export function cardTitle(wish) { return wish.trim().slice(0, 100); }

function isScenarioLogin(config, username) {
  return String(username).startsWith(`${config.loginPrefix}-`);
}

function inviteToken(link) {
  try { return new URL(link, 'http://studio.invalid').searchParams.get('invite'); } catch { return null; }
}

function sameAddress(a, b) {
  return String(a ?? '').replace(/\/+$/, '') === String(b ?? '').replace(/\/+$/, '');
}

function environment(view, kind) {
  return view?.environments?.find(item => item.declaration?.kind === kind) ?? null;
}

function poll(ctx, timeoutMs) {
  return { clock: ctx.clock, timeoutMs, intervalMs: ctx.config.pollMs };
}

function excerpt(text) {
  return typeof text === 'string' ? text.replace(/\s+/g, ' ').trim().slice(0, 300) : null;
}
