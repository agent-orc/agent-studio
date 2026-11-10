// A stand-in Studio for the product scenario tests. It serves the routes the
// scenario and the product controls call on a loopback port, with "Click
// Counter" already live on stage and production as the lifecycle scenario
// leaves it. Cards, previews, releases and environments move forward one stage
// per read, so a whole playthrough runs in milliseconds on a fake clock.
import { createHash, randomBytes } from 'node:crypto';
import http from 'node:http';

import { SessionClient } from '../session.mjs';

const SESSION_COOKIE = 'agentstudio-session';
const ADVANCE = { '2-ready': '3-progress', '3-progress': '4-auto-review', '4-auto-review': '5-human-review' };
const PREVIEW_ADVANCE = { queued: 'deploying', deploying: 'ready', updating: 'deploying', removing: 'removed' };
const STAGE = 'http://v1.test:8081';
const PRODUCTION = 'http://v1.test:8082';
const REFUSED = ['/api/v1/management/credential-inventory', '/api/secrets', '/api/v1/management/remote-hosts', '/api/projects/settings'];
const FEEDBACK_HEADING = "Preview feedback for this card's next fix round:";

export async function startFakeStudio({
  clock,
  login = 'demo-owner',
  password = randomBytes(12).toString('hex'),
  ownerRole = 'owner',
  projectLive = true,
  feedbackDenied = false,
  previewFails = false,
  fixIgnoresFeedback = false,
  reachable = [],
  tokensPerRound = 45_000,
} = {}) {
  const sessions = new Map();
  const requests = [];
  const users = [{ id: 'u-owner', username: login, displayName: 'Owner', role: ownerRole, projects: ['*'], disabled: false,
    mustChangePassword: false, productionApprovalProjects: [], password }];
  const invitations = new Map();
  const bindings = [];
  const workspaces = [{ id: 'ws-1', displayName: 'Department demo', isDefault: false }];
  const quota = { modelTokens: 120_000, computeCoreHours: 1.5, hostingSlots: 2 };
  const tasks = new Map();
  const live = createHash('sha256').update('lifecycle').digest('hex');
  const project = {
    id: 'PROJ-101', displayName: 'Click Counter', shortCode: 'CC', workspaceId: 'ws-1', urls: [], cardCount: 4,
    prompts: new Map(), releases: [], promotions: [],
    environments: {
      stage: { deployedArtifact: `click-counter@sha256:${live}`, health: 'healthy', history: [] },
      production: { deployedArtifact: projectLive ? `click-counter@sha256:${live}` : null, health: projectLive ? 'healthy' : 'unknown', history: [] },
    },
  };
  const projects = [project];
  let counter = 0;
  const nextId = prefix => `${prefix}-${++counter}`;
  const now = () => clock.now();

  // The page behind each delivery: the reset button shrinks when a fix round applies the feedback.
  const sites = new Map([[STAGE, { reset: { width: 120, height: 40 } }], [PRODUCTION, { reset: { width: 120, height: 40 } }]]);

  function findProject(handle) {
    return projects.find(item => item.id === handle || item.displayName === handle || item.shortCode === handle) ?? null;
  }

  function sha(text) { return createHash('sha1').update(text).digest('hex'); }

  function deliver(card) {
    card.round += 1;
    card.deliverySha = sha(`${card.id}:${card.round}`);
    const smaller = card.round > 1 && card.feedback && !fixIgnoresFeedback;
    card.reset = smaller ? { width: 80, height: 28 } : { width: 120, height: 40 };
    card.preview = card.preview
      ? { ...card.preview, status: 'updating', deliverySha: card.deliverySha, screenshotUrl: null, jobId: nextId('job') }
      : { address: `https://${card.id}.click-counter.preview.internal`, status: 'queued', deliverySha: card.deliverySha,
        screenshotUrl: null, updatedAtUtc: now().toISOString(), expiresAtUtc: new Date(now().getTime() + 7 * 86_400_000).toISOString(),
        jobId: nextId('job') };
    card.screenshots.push(
      shot(card, `before-${card.round}.png`, 'Before: home page with the counter'),
      shot(card, `after-${card.round}.png`, 'After: home page with reset confirmation and history'));
    quota.modelTokens += tokensPerRound;
    quota.computeCoreHours = Math.round((quota.computeCoreHours + 0.2) * 100) / 100;
  }

  function shot(card, fileName, caption) {
    return { jobId: card.id, fileName, relativePath: `results/${fileName}`, caption, status: 'passed',
      url: `/api/tasks/${card.id}/screenshot?path=results%2F${fileName}` };
  }

  function advance(card) {
    const next = ADVANCE[card.state];
    if (next) {
      if (card.state === '4-auto-review') deliver(card);
      card.state = next;
      return;
    }
    if (card.preview && PREVIEW_ADVANCE[card.preview.status]) {
      const status = PREVIEW_ADVANCE[card.preview.status];
      if (status === 'ready' && previewFails) { card.preview.status = 'failed'; return; }
      card.preview.status = status;
      if (status === 'ready') {
        card.preview.screenshotUrl = `/api/projects/${project.displayName}/previews/${card.key}/screenshot?delivery=${card.deliverySha}`;
        sites.set(card.preview.address, { reset: card.reset });
      }
    }
  }

  function integrate(card) {
    if (card.preview) card.preview.status = 'removing';
    const digest = createHash('sha256').update(`${card.id}:${card.round}:release`).digest('hex');
    const release = { id: nextId('release'), version: `2026.11.09.${project.releases.length + 2}`, commitSha: digest.slice(0, 40),
      artifactDigests: [`click-counter@sha256:${digest}`], cardKeys: [card.key], createdAtUtc: now().toISOString() };
    project.releases.push(release);
    const artifact = release.artifactDigests[0];
    project.promotions.push({ artifact, stageId: 'stage', productionId: 'production', status: 'pending' });
    project.environments.stage.history.unshift({ at: now().toISOString(), status: 'healthy', artifact });
    project.environments.stage.deployedArtifact = artifact;
    project.artifactSites = { ...(project.artifactSites ?? {}), [artifact]: card.reset };
    sites.set(STAGE, { reset: card.reset });
  }

  function environmentsView() {
    const declaration = (kind, address) => ({ id: kind, name: kind, kind, targetHostId: 'v1', address, template: 'static' });
    return {
      project: project.displayName,
      environments: [
        { declaration: declaration('stage', STAGE), ...project.environments.stage, history: [...project.environments.stage.history] },
        { declaration: declaration('production', PRODUCTION), ...project.environments.production, history: [...project.environments.production.history] },
      ],
      promotions: project.promotions.map(item => ({ ...item })),
      jobs: [...tasks.values()].filter(card => card.preview).map(card => ({
        kind: 'preview', environmentId: `preview-${card.id}`, cardKey: card.key, status: card.preview.status,
        error: card.preview.status === 'failed' ? 'Preview run zone is not configured.' : null,
      })),
      failureCards: [],
    };
  }

  function userView(user) {
    const { password: _secret, ...visible } = user;
    return { ...visible, projects: [...visible.projects], productionApprovalProjects: [...visible.productionApprovalProjects] };
  }

  function openSession(user) {
    const token = randomBytes(16).toString('hex');
    const csrf = randomBytes(16).toString('hex');
    sessions.set(token, { csrf, userId: user.id });
    return { token, csrf };
  }

  function addCard(fields) {
    project.cardCount += 1;
    const card = {
      id: fields.id ?? nextId('card'), key: `CC-${project.cardCount}`, title: fields.title, state: fields.state ?? '0-backlog',
      projectName: project.displayName, projectId: project.id, revision: 1, round: 0, screenshots: [], preview: null,
      feedback: null, createdBy: fields.createdBy ?? null, acceptedBy: null,
    };
    tasks.set(card.id, card);
    project.prompts.set(card.id, fields.prompt ?? '');
    return card;
  }

  function cardView(card) {
    const { revision, screenshots, feedback, reset, round, deliverySha, ...visible } = card;
    return { ...visible, preview: card.preview ? { ...card.preview } : null };
  }

  async function route(request, url, body, raw, session) {
    const parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
    const method = request.method;
    const key = `${method} /${parts.join('/')}`;
    const json = (status, value, headers = {}) => ({ status, value, headers });
    const user = session ? users.find(item => item.id === session.userId) : null;
    const contributor = user?.role === 'contributor';
    const signed = (account, extra = {}) => {
      const { token, csrf } = openSession(account);
      return json(200, { profile: 'networked', authenticated: true, user: userView(account), csrfToken: csrf, ...extra },
        { 'Set-Cookie': `${SESSION_COOKIE}=${token}; Path=/; HttpOnly; SameSite=Strict` });
    };

    if (key === 'POST /api/auth/login') {
      if (request.headers['sec-fetch-site'] !== 'same-origin') return json(403, { error: 'origin-required' });
      const account = users.find(item => item.username === body.username && item.password === body.password && !item.disabled);
      return account ? signed(account) : json(401, { error: 'invalid-credentials' });
    }
    if (key === 'POST /api/auth/invite/accept') {
      const invitation = invitations.get(body.token);
      if (!invitation || invitation.used) return json(401, { error: 'invalid-invitation' });
      const account = users.find(item => item.id === invitation.userId);
      if (String(body.newPassword ?? '').length < 12) return json(400, { error: 'password-policy' });
      invitation.used = true;
      account.password = body.newPassword;
      return signed(account);
    }
    if (!session) return json(401, { error: 'unauthenticated' });
    if (method !== 'GET' && request.headers['x-csrf-token'] !== session.csrf) return json(403, { error: 'csrf-invalid' });
    if (key === 'POST /api/auth/logout') {
      sessions.delete(session.token);
      return json(200, {}, { 'Set-Cookie': `${SESSION_COOKIE}=; Path=/; Max-Age=0` });
    }

    // The Product role (ContributorAccessPolicy): credentials, hosts, settings and user admin are refused.
    if (contributor && method === 'GET' && REFUSED.includes(url.pathname) && !reachable.includes(url.pathname))
      return json(403, { error: 'role-denied', message: 'The Product role cannot perform this action.' });
    if (contributor && parts[1] === 'auth') return json(403, { error: 'role-denied', message: 'The Product role cannot perform this action.' });
    if (REFUSED.includes(url.pathname)) return json(200, []);

    const ownerOnly = () => user.role === 'owner' ? null : json(403, { error: 'owner-required' });
    if (key === 'POST /api/auth/invitations') {
      if (ownerOnly()) return ownerOnly();
      if (users.some(item => item.username === body.username)) return json(409, { error: 'username-exists' });
      const account = { id: nextId('u'), username: body.username, displayName: body.displayName, role: body.role,
        projects: body.projects ?? [], disabled: false, mustChangePassword: true, productionApprovalProjects: [], password: null };
      users.push(account);
      for (const scoped of account.projects)
        bindings.push({ id: nextId('rb'), principalKind: 'user', principalId: account.id, role: account.role, scope: { kind: 'project', project: scoped } });
      const token = `inv.${randomBytes(6).toString('hex')}.${randomBytes(16).toString('hex')}`;
      invitations.set(token, { userId: account.id, used: false });
      return json(200, { user: userView(account), signInLink: `/?invite=${encodeURIComponent(token)}`, expiresAt: new Date(now().getTime() + 86_400_000).toISOString() });
    }
    if (key === 'GET /api/auth/users') return ownerOnly() ?? json(200, users.map(userView));
    if (method === 'PUT' && parts[1] === 'auth' && parts[2] === 'users' && parts.length === 4) {
      if (ownerOnly()) return ownerOnly();
      const account = users.find(item => item.id === parts[3]);
      if (!account) return json(404, { error: 'user-not-found' });
      if (body.productionApprovalProjects) {
        if (body.productionApprovalProjects.some(item => !account.projects.includes(item))) return json(400, { error: 'invalid-production-approval' });
        account.productionApprovalProjects = [...body.productionApprovalProjects];
      }
      if (body.disabled !== undefined && body.disabled !== null) {
        account.disabled = body.disabled;
        for (const [token, entry] of sessions) if (entry.userId === account.id) sessions.delete(token);
      }
      return json(200, userView(account));
    }
    if (key === 'GET /api/auth/iam') return ownerOnly() ?? json(200, { bindings: bindings.map(item => ({ ...item })), invitations: [] });
    if (method === 'DELETE' && parts[1] === 'auth' && parts[2] === 'iam' && parts[3] === 'bindings') {
      const index = bindings.findIndex(item => item.id === parts[4]);
      if (index < 0) return json(404, { error: 'binding-not-found' });
      bindings.splice(index, 1);
      return json(204, null);
    }

    if (key === 'GET /api/workspaces') return json(200, workspaces.map(ws => ({ ...ws, projects: projects.filter(item => item.workspaceId === ws.id).map(summary) })));
    if (key === 'GET /api/projects') return json(200, projects.map(summary));
    if (method === 'GET' && parts[1] === 'workspaces' && parts[3] === 'quota')
      return json(200, { workspaceId: parts[2], usage: { ...quota }, limits: { modelTokensPerWeek: 2_000_000, computeCoreHoursPerWeek: 40, hostingSlots: 3 }, status: 'ok' });
    if (parts[1] === 'projects' && parts[3] === 'urls') {
      if (ownerOnly()) return ownerOnly();
      if (method === 'POST' && parts.length === 4) {
        project.urls.push({ id: `url-${project.urls.length + 1}`, label: body.label, url: body.url, sortOrder: project.urls.length });
        return json(200, summary(project));
      }
      if (method === 'DELETE' && parts.length === 5) {
        project.urls = project.urls.filter(item => item.id !== parts[4]);
        return json(200, summary(project));
      }
    }
    if (method === 'POST' && parts[1] === 'runner' && parts[3] === 'orchestrator-chat') {
      const product = body.navigationContext?.observedSurface === 'URL preview';
      return json(200, { project: project.displayName, reply: { role: 'orchestrator', text: product
        ? 'Happy to help. Where should the confirmation appear, and is there an example you like? Expect roughly 20,000 to 60,000 tokens. Use "Create this change" when you are ready.'
        : 'Noted.' } });
    }

    if (parts[1] === 'tasks') {
      if (key === 'GET /api/tasks') return json(200, [...tasks.values()].map(cardView));
      if (key === 'POST /api/tasks') {
        if (contributor && !['0-backlog', '2-ready'].includes(body.targetState)) return json(403, { error: 'role-denied' });
        const card = addCard({ title: body.title, state: body.targetState, prompt: body.promptMarkdown, createdBy: user.username });
        return json(200, { id: card.id });
      }
      const card = tasks.get(parts[2]);
      if (!card) return json(404, { error: 'not-found' });
      if (method === 'GET' && parts.length === 3) {
        advance(card);
        return json(200, { info: cardView(card) });
      }
      if (method === 'GET' && parts[3] === 'screenshots') return json(200, { jobId: card.id, screenshots: card.screenshots.map(item => ({ ...item })) });
      if (method === 'GET' && parts[3] === 'files' && parts[4] === 'prompt.md')
        return { status: 200, text: project.prompts.get(card.id), headers: { 'Content-Type': 'text/plain; charset=utf-8' } };
      if (method === 'POST' && parts[3] === 'attention') return json(200, { revision: String(card.revision) });
      if (method === 'POST' && parts[3] === 'move') {
        if (request.headers['if-match'] !== String(card.revision)) return json(412, { error: 'stale-card-revision' });
        if (contributor && !(card.state === '5-human-review' && body.targetState === '6-completed') && !['0-backlog', '2-ready'].includes(body.targetState))
          return json(403, { error: 'role-denied' });
        card.revision += 1;
        card.state = body.targetState;
        if (body.targetState === '6-completed') { card.acceptedBy = user.username; integrate(card); }
        if (body.targetState === '7-archive' && card.preview) card.preview.status = 'removing';
        return json(200, { id: card.id, state: card.state });
      }
      if (method === 'POST' && parts[3] === 'preview-feedback') {
        if (contributor && feedbackDenied) return json(403, { error: 'role-denied', message: 'The Product role cannot perform this action.' });
        const form = await new Request('http://fake.invalid', { method: 'POST', headers: { 'content-type': request.headers['content-type'] }, body: raw }).formData();
        const crop = form.get('crop');
        if (!crop || typeof crop === 'string' || !form.get('comment')) return json(400, { error: 'A screenshot crop and comment are required.' });
        card.feedback = { comment: form.get('comment'), selector: form.get('selector'), previewAddress: form.get('previewAddress') };
        const lines = [FEEDBACK_HEADING, `Preview address: ${card.feedback.previewAddress}`,
          ...(card.feedback.selector ? [`Element selector: ${card.feedback.selector}`] : []),
          `Screenshot crop: attachments/${randomBytes(4).toString('hex')}.png`, 'Requested change:', card.feedback.comment,
          'Use the crop and preview address to locate the affected area. Keep this work on the same card.'];
        project.prompts.set(card.id, `${project.prompts.get(card.id)}\n\n---\n\n## Continuous Session Note - 2026-11-09 08:30\n\n${lines.join('\n')}\n`);
        card.state = '3-progress';
        card.revision += 1;
        return json(202, { status: 'queued' });
      }
    }

    if (parts[1] === 'projects' && parts.length >= 4 && findProject(parts[2])) {
      if (method === 'GET' && parts[3] === 'environments' && parts.length === 4) return json(200, environmentsView());
      if (method === 'GET' && parts[3] === 'deploy' && parts[4] === 'releases') return json(200, project.releases.map(item => ({ ...item })));
      if (method === 'POST' && parts[3] === 'environments' && parts[4] === 'promotions' && parts[5] === 'approve') {
        if (contributor && !user.productionApprovalProjects.includes(project.id))
          return json(403, { error: 'role-denied', message: 'The Product role cannot perform this action.' });
        const promotion = [...project.promotions].reverse().find(item => item.artifact === body.artifact && item.status === 'pending');
        if (!promotion) return json(409, { error: 'Promotion is not pending.' });
        promotion.status = 'deployed';
        project.environments.production.history.unshift({ at: now().toISOString(), status: 'healthy', artifact: body.artifact, approvedBy: user.username });
        project.environments.production.deployedArtifact = body.artifact;
        project.environments.production.health = 'healthy';
        sites.set(PRODUCTION, { reset: project.artifactSites[body.artifact] });
        return json(200, {});
      }
    }
    return json(404, { error: `fake-studio has no route ${key}` });
  }

  function summary(item) {
    return { id: item.id, displayName: item.displayName, shortCode: item.shortCode, workspaceId: item.workspaceId, urls: item.urls.map(url => ({ ...url })) };
  }

  const server = http.createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const raw = Buffer.concat(chunks);
    const text = raw.toString('utf8');
    const url = new URL(request.url, 'http://127.0.0.1');
    const cookie = (request.headers.cookie ?? '').split(';').map(part => part.trim().split('='))
      .find(([name]) => name === SESSION_COOKIE)?.[1];
    const session = cookie && sessions.has(cookie) ? { ...sessions.get(cookie), token: cookie } : null;
    let body = {};
    if (/json/.test(request.headers['content-type'] ?? '')) {
      try { body = text ? JSON.parse(text) : {}; } catch { body = {}; }
    }
    requests.push({ method: request.method, path: url.pathname, body: text, headers: { ...request.headers } });
    const answer = await route(request, url, body, raw, session);
    response.writeHead(answer.status, { 'Content-Type': 'application/json', ...answer.headers });
    response.end(answer.text ?? (answer.value === null ? '' : JSON.stringify(answer.value)));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  return {
    url: `http://127.0.0.1:${port}`,
    login, password, requests, users, bindings, project, tasks, sites, quota,
    stage: STAGE, production: PRODUCTION,
    close: () => new Promise(resolve => server.close(resolve)),
  };
}

/** A clock whose sleep advances time at once. */
export function fakeClock(start = '2026-11-09T08:00:00Z') {
  let current = new Date(start).getTime();
  return {
    now: () => new Date(current),
    sleep: async ms => { current += Math.max(1, ms); },
  };
}

/**
 * A browser stand-in: one page per person, each with its own Studio session.
 * The pages act like the product controls (product-feedback.component): the
 * draft questions, the proposal, the card created on confirmation, the marked
 * feedback sent as multipart, and the acceptance move.
 */
export function fakeBrowserFactory(studio) {
  const calls = [];
  const factory = async ({ reportDir }) => {
    const { mkdir, writeFile } = await import('node:fs/promises');
    const path = await import('node:path');
    await mkdir(path.join(reportDir, 'screenshots'), { recursive: true });
    let counter = 0;
    const pages = new Map();

    const personPage = person => {
      if (pages.has(person)) return pages.get(person);
      const session = new SessionClient({ url: studio.url, clientId: `browser-${person}` });
      const ui = { draft: 'closed', fields: {}, selectedId: null, box: null, feedbackShots: null, last: null, projectUrl: null };
      const card = async () => ui.selectedId
        ? (await session.send('GET', `/api/tasks/${ui.selectedId}`, { query: { project: studio.project.id } })).body?.info : null;
      const shots = async () => ui.selectedId
        ? (await session.get(`/api/tasks/${ui.selectedId}/screenshots`, { project: studio.project.id })).screenshots : [];
      const page = {
        async redeemInvitation(link, password) {
          calls.push([person, 'redeem']);
          const token = new URL(link, studio.url).searchParams.get('invite');
          const answer = await session.send('POST', '/api/auth/invite/accept', { body: { token, newPassword: password } });
          if (answer.status !== 200) throw new Error(`Studio refused the one-time sign-in link (${answer.status}).`);
          session.csrfToken = answer.body.csrfToken;
        },
        async openStudio(route) { calls.push([person, 'openStudio', route]); },
        async openProjectUrl(projectName, urlId) {
          calls.push([person, 'openProjectUrl', projectName, urlId]);
          ui.projectUrl = studio.project.urls.find(item => item.id === urlId)?.url ?? null;
          if (!ui.projectUrl) throw new Error(`No Project URL ${urlId}.`);
          Object.assign(ui, { draft: 'closed', selectedId: null, box: null });
        },
        async click(testId) {
          calls.push([person, 'click', testId]);
          if (testId === 'product-start-change') ui.draft = 'wish';
          if (testId === 'product-confirm-change') {
            const prompt = [`Change request: ${ui.fields['product-wish']}`, `Where: ${ui.fields['product-location']}`,
              `Preview address: ${ui.projectUrl}`, 'Capture screenshots of every changed page before and after the delivery.'].join('\n\n');
            const answer = await session.send('POST', '/api/tasks', { body: { title: ui.fields['product-wish'].trim().slice(0, 100),
              targetState: '2-ready', taskType: 'feature', promptMarkdown: prompt } });
            ui.last = { status: answer.status, body: JSON.stringify(answer.body) };
            if (answer.status === 200) { ui.draft = 'closed'; ui.selectedId = answer.body.id; }
          }
          if (testId === 'product-send-feedback') {
            const form = new FormData();
            form.set('crop', new Blob([PNG], { type: 'image/png' }), 'preview-feedback.png');
            form.set('comment', ui.fields['product-comment'] ?? '');
            form.set('previewAddress', ui.projectUrl);
            form.set('selector', ui.fields['product-selector'] ?? '');
            const encoded = new Response(form);
            const contentType = encoded.headers.get('content-type');
            const raw = Buffer.from(await encoded.arrayBuffer());
            const answer = await session.send('POST', `/api/tasks/${ui.selectedId}/preview-feedback`, { form: raw, headers: { 'Content-Type': contentType } });
            ui.last = { status: answer.status, body: raw.toString('latin1'), answer: JSON.stringify(answer.body) };
            if (answer.status < 300) ui.feedbackShots = (await shots()).map(item => item.url);
          }
          if (testId === 'product-check-update') {
            const current = (await shots()).map(item => item.url);
            if (ui.feedbackShots && current.some(item => !ui.feedbackShots.includes(item))) ui.feedbackShots = null;
          }
          if (testId === 'product-accept') {
            const attention = await session.request('POST', `/api/tasks/${ui.selectedId}/attention`, { query: { project: studio.project.id } });
            const answer = await session.send('POST', `/api/tasks/${ui.selectedId}/move`, { query: { project: studio.project.id },
              body: { targetState: '6-completed' }, headers: { 'If-Match': attention.revision } });
            ui.last = { status: answer.status, body: JSON.stringify({ targetState: '6-completed' }) };
          }
        },
        async fill(testId, value) { ui.fields[testId] = value; },
        async select(testId, value) { ui.selectedId = value; ui.box = null; },
        async clickButtonIn(testId, name) {
          if (name === 'Continue' && ui.draft === 'wish' && ui.fields['product-wish']) ui.draft = 'location';
          else if (name === 'Continue' && ui.draft === 'location' && ui.fields['product-location']) ui.draft = 'example';
          else if (name === 'Review change' && ui.draft === 'example') ui.draft = 'confirm';
        },
        async clickText() {},
        async testIdState(testId) {
          const show = text => ({ visible: true, text });
          const hide = { visible: false, text: null };
          if (testId === 'auth-gate') return session.csrfToken ? hide : show('Sign in');
          if (testId === 'product-draft')
            return { location: show('Where on the page should it change? Continue'), example: show('Is there an example or detail we should match? Review change') }[ui.draft] ?? hide;
          if (testId === 'product-proposal') return ui.draft === 'confirm'
            ? show(`Proposed change ${ui.fields['product-wish']} on ${ui.fields['product-location']}. Estimated quota: one coding run and one review run, roughly 20,000 to 60,000 tokens.`) : hide;
          const current = await card();
          if (testId === 'product-status') return current ? show({ '5-human-review': 'Ready to look at', '6-completed': 'Accepted' }[current.state] ?? 'In progress') : hide;
          if (testId === 'product-before-after') {
            const list = await shots();
            return list.some(item => /before/i.test(item.caption)) && list.some(item => /after/i.test(item.caption)) ? show('Before After') : hide;
          }
          if (testId === 'product-selection') return ui.box ? show('') : hide;
          if (testId === 'product-accept') return current?.state === '5-human-review' && !ui.feedbackShots ? show('Accept this preview') : hide;
          return hide;
        },
        async bodyText() { return 'Your account cannot view these settings.'; },
        async imageSize() { return { width: 1440, height: 900, src: (await shots()).at(-1)?.url ?? null }; },
        async markImage(testId, box) { ui.box = box; calls.push([person, 'mark', box]); },
        async captureRequest(method, suffix, action) { ui.last = null; await action(); return ui.last; },
        async measureButton(address) {
          const site = studio.sites.get(address);
          if (!site) return { status: 404, found: false, buttons: [] };
          return { status: 200, found: true, name: 'Reset', buttons: ['+1', 'Reset'],
            box: { x: 0.45, y: 0.5, width: site.reset.width / 1440, height: site.reset.height / 900 },
            pixels: { ...site.reset }, selector: '#reset', className: 'reset', style: { fontSize: '16px' } };
        },
        async openAddress(address) { calls.push([person, 'openAddress', address]); return { status: 200, text: 'Count 0' }; },
        async screenshot(name) {
          counter += 1;
          const file = `${String(counter).padStart(2, '0')}-${person}-${name}.png`;
          await writeFile(path.join(reportDir, 'screenshots', file), PNG);
          return `screenshots/${file}`;
        },
      };
      pages.set(person, page);
      return page;
    };
    return { page: async person => personPage(person), async close() { calls.push(['close']); } };
  };
  factory.calls = calls;
  return factory;
}

// A 1x1 transparent PNG.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');
