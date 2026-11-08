// A stand-in Studio for the lifecycle scenario tests. It serves the routes the
// scenario calls on a loopback port and moves cards, releases and
// environments forward one stage per read, so a whole playthrough runs in
// milliseconds on a fake clock.
import { createHash, randomBytes } from 'node:crypto';
import http from 'node:http';

import { totp } from '../session.mjs';

const SESSION_COOKIE = 'agentstudio-session';
const ADVANCE = { '2-ready': '3-progress', '3-progress': '4-auto-review', '4-auto-review': '5-human-review' };

export async function startFakeStudio({
  clock,
  login = 'demo-owner',
  password = randomBytes(12).toString('hex'),
  role = 'owner',
  totpSecret = null,
  chatNeedsConfirmation = false,
  redReviewTitles = [],
  stageFailsFor = [],
  tokensPerCard = 60_000,
  coreHoursPerCard = 0.25,
} = {}) {
  const sessions = new Map();
  const usedTotpWindows = new Set();
  const requests = [];
  const workspaces = [{ id: 'default', displayName: 'Default', isDefault: true }];
  const projects = [];
  const quotas = new Map();
  const tasks = new Map();
  let counter = 0;

  const now = () => clock.now();
  const nextId = prefix => `${prefix}-${++counter}`;

  function project(name) {
    return projects.find(item => item.displayName === name || item.id === name || item.shortCode === name) ?? null;
  }

  function quotaSnapshot(workspaceId) {
    const quota = quotas.get(workspaceId) ?? { limits: null, usage: { modelTokens: 0, modelCostUsd: 0, computeCoreHours: 0, hostingSlots: 0 } };
    const limits = quota.limits;
    const share = limits?.modelTokensPerWeek ? quota.usage.modelTokens / limits.modelTokensPerWeek : 0;
    const coreShare = limits?.computeCoreHoursPerWeek ? quota.usage.computeCoreHours / limits.computeCoreHoursPerWeek : 0;
    const used = Math.max(share, coreShare);
    const status = used >= 1 ? 'blocked' : used >= 0.8 ? 'warning' : 'ok';
    const remaining = {
      modelTokens: limits?.modelTokensPerWeek ? Math.max(0, limits.modelTokensPerWeek - quota.usage.modelTokens) : 0,
      modelCostUsd: 0,
      computeCoreHours: limits?.computeCoreHoursPerWeek ? Math.max(0, limits.computeCoreHoursPerWeek - quota.usage.computeCoreHours) : 0,
      hostingSlots: limits?.hostingSlots ? Math.max(0, limits.hostingSlots - quota.usage.hostingSlots) : 0,
    };
    return {
      workspaceId, projectId: null, weekStartUtc: '2026-11-09T00:00:00Z', weekEndUtc: '2026-11-16T00:00:00Z',
      limits, usage: { ...quota.usage }, remaining, status,
      message: status === 'blocked' ? 'Weekly quota reached. New work is paused until Monday 00:00 UTC or an owner override.'
        : status === 'warning' ? 'Quota is at least 80% used.' : null,
      biggestConsumers: [], biggestProjects: [], dailyTrend: [], meterComplete: true, overrideActive: false,
      codingAllowed: status !== 'blocked', hostingAllowed: true,
    };
  }

  function addCard(owner, fields) {
    owner.cardCount += 1;
    const card = {
      id: fields.id ?? nextId('card'), key: `${owner.shortCode}-${owner.cardCount}`, title: fields.title,
      state: fields.state ?? '0-backlog', kind: 'task', tags: fields.tags ?? [], creationSource: fields.creationSource ?? null,
      projectName: owner.displayName, projectId: owner.id, prompt: fields.prompt ?? '', revision: 1,
      parkedBlocker: null, needsInput: null, orchestratorVerdict: null, steerPendingSince: null,
    };
    tasks.set(card.id, card);
    return card;
  }

  function charge(owner) {
    const quota = quotas.get(owner.workspaceId);
    if (!quota) return;
    quota.usage.modelTokens += tokensPerCard;
    quota.usage.computeCoreHours = Math.round((quota.usage.computeCoreHours + coreHoursPerCard) * 100) / 100;
  }

  function integrate(card) {
    const owner = project(card.projectId);
    if (card.id === 'lifecycle-environments') { owner.declared = true; owner.healthFile = true; }
    if (card.id === 'lifecycle-seeded-fault') owner.healthFile = false;
    if (card.id === 'lifecycle-fix-production-health') owner.healthFile = true;
    if (!owner.declared) return;
    const digest = createHash('sha256').update(`${card.id}:${owner.releases.length}`).digest('hex');
    const release = {
      id: nextId('release'), version: `2026.11.09.${owner.releases.length + 1}`, commitSha: digest.slice(0, 40),
      artifactDigests: [`click-counter@sha256:${digest}`], cardKeys: [card.key], channel: 'default',
      createdAtUtc: new Date(now().getTime() + owner.releases.length).toISOString(), healthFile: owner.healthFile,
    };
    owner.releases.push(release);
    const artifact = release.artifactDigests[0];
    const failsStage = stageFailsFor.includes(card.title);
    owner.promotions.push({ artifact, stageId: 'stage', productionId: 'production', status: failsStage ? 'failed' : 'pending' });
    deploy(owner, 'stage', artifact, failsStage ? 'failed' : 'healthy');
  }

  function deploy(owner, environmentId, artifact, status) {
    const env = owner.environments[environmentId];
    env.history.unshift({ at: now().toISOString(), status, artifact, previousArtifact: env.deployedArtifact, evidence: status === 'failed' ? 'health-check-failed' : null });
    if (status !== 'failed') { env.deployedArtifact = artifact; env.health = 'healthy'; }
  }

  function approve(owner, artifact) {
    const promotion = [...owner.promotions].reverse().find(item => item.artifact === artifact && item.status === 'pending');
    if (!promotion) return false;
    const release = owner.releases.find(item => item.artifactDigests.includes(artifact));
    const production = owner.environments.production;
    if (release.healthFile) {
      promotion.status = 'deployed';
      deploy(owner, 'production', artifact, 'healthy');
      return true;
    }
    promotion.status = 'failed';
    const previous = production.deployedArtifact;
    deploy(owner, 'production', artifact, 'failed');
    production.history.unshift({ at: now().toISOString(), status: 'rolled-back', artifact: previous, previousArtifact: artifact, evidence: null });
    production.deployedArtifact = previous;
    production.health = 'healthy';
    owner.failureCards.unshift({ environmentId: 'production', artifact, at: now().toISOString(), evidence: 'health-check-failed', rollbackArtifact: previous });
    addCard(owner, { id: `production-deploy-failed-${owner.releases.length}`, title: `Investigate Production deployment of release ${release.version}`,
      tags: ['production-deployment'], creationSource: 'deployment' });
    return true;
  }

  function environmentsView(owner) {
    const declaration = (kind, address) => ({ id: kind, name: kind, kind, targetHostId: 'v1', address, template: 'static' });
    return {
      project: owner.displayName,
      environments: [
        { declaration: declaration('stage', 'http://v1.test:8081'), ...owner.environments.stage, pendingArtifact: null },
        { declaration: declaration('production', 'http://v1.test:8082'), ...owner.environments.production, pendingArtifact: null },
      ].map(item => ({ ...item, history: [...item.history] })),
      promotions: owner.promotions.map(item => ({ ...item })),
      jobs: [],
      failureCards: owner.failureCards.map(item => ({ ...item })),
    };
  }

  function review(card) {
    const red = redReviewTitles.some(title => card.title.includes(title));
    const attempt = { plane: 'Remote', attemptId: `review-${card.key}`, receivedAt: now().toISOString(),
      outcome: red ? 'Fail' : 'Pass', buildTestsResult: red ? 'failed' : 'passed', aspects: [], subjectSha: 'a'.repeat(40),
      reportRef: `remote-review-${card.key}.md` };
    return { attempts: [attempt], rounds: 1, latestOutcome: attempt.outcome, blockingAspects: [],
      delivery: { status: 'not-attempted' }, decisionRequired: { required: false } };
  }

  async function route(request, url, body, session) {
    const parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
    const method = request.method;
    const key = `${method} /${parts.join('/')}`;
    const json = (status, value, headers = {}) => ({ status, value, headers });

    if (key === 'POST /api/auth/login') {
      if (request.headers['sec-fetch-site'] !== 'same-origin') return json(403, { error: 'studio-origin-rejected' });
      if (body.username !== login || body.password !== password) return json(401, { error: 'invalid-credentials' });
      if (totpSecret) {
        const window = Math.floor(now().getTime() / 30_000);
        if (body.secondFactorCode !== totp(totpSecret, now().getTime()) || usedTotpWindows.has(window))
          return json(401, { error: 'second-factor-required' });
        usedTotpWindows.add(window);
      }
      const token = randomBytes(16).toString('hex');
      const csrf = randomBytes(16).toString('hex');
      sessions.set(token, { csrf });
      return json(200, { profile: 'networked', authenticated: true, user: { username: login, role }, csrfToken: csrf },
        { 'Set-Cookie': `${SESSION_COOKIE}=${token}; Path=/; HttpOnly; SameSite=Strict` });
    }
    if (!session) return json(401, { error: 'unauthenticated' });
    if (method !== 'GET' && request.headers['x-csrf-token'] !== session.csrf) return json(403, { error: 'csrf-token-invalid' });
    if (key === 'POST /api/auth/logout') {
      sessions.delete(session.token);
      return json(200, {}, { 'Set-Cookie': `${SESSION_COOKIE}=; Path=/; Max-Age=0` });
    }
    if (key === 'GET /api/auth/status') return json(200, { profile: 'networked', authenticated: true, user: { role } });

    if (key === 'GET /api/workspaces')
      return json(200, workspaces.map(ws => ({ ...ws, projects: projects.filter(item => item.workspaceId === ws.id).map(summary) })));
    if (key === 'POST /api/workspaces') {
      const ws = { id: nextId('ws'), displayName: body.displayName, isDefault: false };
      workspaces.push(ws);
      return json(201, ws);
    }
    if (method === 'DELETE' && parts[1] === 'workspaces' && parts.length === 3) {
      const index = workspaces.findIndex(ws => ws.id === parts[2]);
      if (index < 0) return json(404, { error: 'unknown workspace' });
      if (projects.some(item => item.workspaceId === parts[2])) return json(409, { error: 'Workspace has projects' });
      workspaces.splice(index, 1);
      return json(200, { deletedId: parts[2] });
    }
    if (parts[1] === 'workspaces' && parts[3] === 'quota' && parts.length === 4) {
      if (method === 'GET') return json(200, quotaSnapshot(parts[2]));
      if (role !== 'owner') return json(403, {});
      const quota = quotas.get(parts[2]) ?? { usage: { modelTokens: 0, modelCostUsd: 0, computeCoreHours: 0, hostingSlots: 0 } };
      quota.limits = { modelTokensPerWeek: body.modelTokensPerWeek, computeCoreHoursPerWeek: body.computeCoreHoursPerWeek, hostingSlots: body.hostingSlots };
      quotas.set(parts[2], quota);
      return json(204, null);
    }
    if (method === 'GET' && parts[1] === 'quota' && parts[2] === 'admission') {
      const owner = project(parts[3]);
      const snapshot = quotaSnapshot(owner.workspaceId);
      return json(200, { allowed: snapshot.codingAllowed, message: snapshot.message, meterUnavailable: false });
    }

    if (key === 'GET /api/projects') return json(200, projects.map(summary));
    if (key === 'POST /api/projects') {
      if (projects.some(item => item.displayName === body.displayName)) return json(409, { error: 'displayName is already used.' });
      const repository = body.createRepository;
      const created = {
        id: `PROJ-${100 + projects.length + 1}`, displayName: body.displayName, shortCode: 'CC', workspaceId: body.workspaceId,
        repositoryUrl: `https://github.com/${repository.owner}/${repository.name}`, cardCount: 0, declared: false, healthFile: false,
        releases: [], promotions: [], failureCards: [],
        environments: { stage: { deployedArtifact: null, health: 'unknown', history: [] }, production: { deployedArtifact: null, health: 'unknown', history: [] } },
      };
      projects.push(created);
      addCard(created, { id: 'hello-world-change', title: 'Hello world change', state: '1-preparation' });
      return json(201, summary(created));
    }
    if (method === 'DELETE' && parts[1] === 'projects' && /^PROJ-\d+$/.test(parts[2] ?? '')) {
      const index = projects.findIndex(item => item.id === parts[2]);
      if (index < 0) return json(404, { error: 'unknown project' });
      for (const [id, card] of tasks) if (card.projectId === parts[2]) tasks.delete(id);
      projects.splice(index, 1);
      return json(200, { deletedId: parts[2] });
    }

    if (method === 'POST' && parts[1] === 'runner' && parts[3] === 'orchestrator-chat') {
      const owner = project(parts[2]);
      owner.chatTurns = (owner.chatTurns ?? 0) + 1;
      if (!chatNeedsConfirmation || owner.chatTurns > 1) {
        addCard(owner, { title: 'Counter with increment and reset', creationSource: 'orchestrator' });
        addCard(owner, { title: 'History of the last ten values', creationSource: 'orchestrator' });
        return json(200, { project: owner.displayName, reply: { text: 'I created two cards.' } });
      }
      return json(200, { project: owner.displayName, reply: { text: 'Shall I create two cards for this?' } });
    }

    if (parts[1] === 'tasks') {
      const owner = project(url.searchParams.get('project') ?? '');
      if (key === 'GET /api/tasks') return json(200, [...tasks.values()].filter(card => card.projectId === owner?.id).map(view));
      if (key === 'POST /api/tasks') {
        const target = project(body.project);
        if (!target) return json(409, { error: 'Job already exists or invalid input' });
        if (tasks.has(body.id)) return json(409, { error: 'Job already exists or invalid input' });
        const card = addCard(target, { id: body.id, title: body.title, state: body.targetState, prompt: body.promptMarkdown, creationSource: body.creationSource });
        return json(200, { id: card.id });
      }
      const card = tasks.get(parts[2]);
      if (!card) return json(404, { error: 'not-found' });
      if (method === 'GET' && parts.length === 3) {
        const next = ADVANCE[card.state];
        if (next) {
          if (card.state === '2-ready' && !quotaSnapshot(project(card.projectId).workspaceId).codingAllowed) return json(200, { info: view(card) });
          if (card.state === '3-progress') charge(project(card.projectId));
          card.state = next;
        }
        return json(200, { info: view(card) });
      }
      if (method === 'GET' && parts[3] === 'review-projection') return json(200, review(card));
      if (method === 'POST' && parts[3] === 'attention') return json(200, { revision: String(card.revision) });
      if (method === 'POST' && parts[3] === 'move') {
        if (request.headers['if-match'] !== String(card.revision)) return json(412, { error: 'stale-card-revision' });
        card.revision += 1;
        card.state = body.targetState;
        if (body.targetState === '6-completed') integrate(card);
        return json(200, { id: card.id, state: card.state });
      }
    }

    if (parts[1] === 'projects' && parts.length >= 4) {
      const owner = project(parts[2]);
      if (!owner) return json(404, {});
      if (key === `GET /api/projects/${owner.displayName}/environments` || (method === 'GET' && parts[3] === 'environments' && parts.length === 4))
        return owner.declared ? json(200, environmentsView(owner)) : json(200, { project: owner.displayName, environments: [], promotions: [], jobs: [], failureCards: [] });
      if (method === 'GET' && parts[3] === 'deploy' && parts[4] === 'releases')
        return json(200, owner.releases.map(({ healthFile, ...release }) => release));
      if (method === 'POST' && parts[3] === 'environments' && parts[4] === 'promotions' && parts[5] === 'approve')
        return approve(owner, body.artifact) ? json(200, {}) : json(409, { error: 'Promotion is not pending.' });
      if (method === 'POST' && parts[3] === 'environments' && parts[5] === 'stop') {
        const env = owner.environments[parts[4]];
        env.history.unshift({ at: now().toISOString(), status: 'stopped', artifact: env.deployedArtifact, previousArtifact: null, evidence: null });
        env.health = 'stopped';
        return json(202, null);
      }
    }
    return json(404, { error: `fake-studio has no route ${key}` });
  }

  function summary(item) {
    return { id: item.id, displayName: item.displayName, shortCode: item.shortCode, workspaceId: item.workspaceId, repositoryUrl: item.repositoryUrl };
  }

  function view(card) {
    const { prompt, revision, ...visible } = card;
    return { ...visible };
  }

  const server = http.createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const text = Buffer.concat(chunks).toString('utf8');
    const url = new URL(request.url, 'http://127.0.0.1');
    const cookie = (request.headers.cookie ?? '').split(';').map(part => part.trim().split('='))
      .find(([name]) => name === SESSION_COOKIE)?.[1];
    const session = cookie && sessions.has(cookie) ? { ...sessions.get(cookie), token: cookie } : null;
    let body = {};
    try { body = text ? JSON.parse(text) : {}; } catch { body = {}; }
    requests.push({ method: request.method, path: url.pathname, body: text, headers: { ...request.headers } });
    const answer = await route(request, url, body, session);
    response.writeHead(answer.status, { 'Content-Type': 'application/json', ...answer.headers });
    response.end(answer.value === null ? '' : JSON.stringify(answer.value));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const { port } = server.address();
  return {
    url: `http://127.0.0.1:${port}`,
    login, password, requests, projects, workspaces, tasks,
    quota: workspaceId => quotaSnapshot(workspaceId),
    close: () => new Promise(resolve => server.close(resolve)),
  };
}

/** A clock whose sleep advances time at once. */
export function fakeClock(start = '2026-11-09T08:00:00Z') {
  let current = new Date(start).getTime();
  return {
    now: () => new Date(current),
    sleep: async ms => { current += Math.max(1, ms); },
    advance: ms => { current += ms; },
  };
}

/** A browser stand-in: answers from the fake Studio and writes small PNG files. */
export function fakeBrowserFactory(studio, { stageStatus = 200, buttons = ['+1', 'Reset'] } = {}) {
  const calls = [];
  const factory = async ({ reportDir }) => {
    const { mkdir, writeFile } = await import('node:fs/promises');
    const path = await import('node:path');
    await mkdir(path.join(reportDir, 'screenshots'), { recursive: true });
    let counter = 0;
    let signedIn = false;
    let workspaceId = null;
    return {
      async signIn(credentials) { calls.push(['signIn', credentials.login]); signedIn = true; },
      async openStudio(route) {
        calls.push(['openStudio', route]);
        const id = route.match(/PROJ-\d+/)?.[0];
        workspaceId = studio.projects.find(item => item.id === id)?.workspaceId ?? workspaceId;
      },
      async openAddress(address) { calls.push(['openAddress', address]); return { status: stageStatus, text: 'Count 0' }; },
      async buttonNames() { return buttons; },
      async clickButton(name) { calls.push(['click', name]); return name === 'Reset' ? 'Count 0' : 'Count 1'; },
      async testIdState(testId) {
        if (testId === 'auth-gate') return { visible: !signedIn, status: null, text: null };
        if (testId === 'workspace-quota') return { visible: true, status: 'ok', text: 'tokens used' };
        if (testId === 'workspace-quota-status') {
          const quota = workspaceId ? studio.quota(workspaceId) : null;
          const visible = Boolean(quota?.message);
          return { visible, status: visible ? quota.status : null, text: visible ? quota.message : null };
        }
        return { visible: false, status: null, text: null };
      },
      async screenshot(name) {
        counter += 1;
        const file = `${String(counter).padStart(2, '0')}-${name}.png`;
        await writeFile(path.join(reportDir, 'screenshots', file), PNG);
        return `screenshots/${file}`;
      },
      async close() { calls.push(['close']); },
    };
  };
  factory.calls = calls;
  return factory;
}

// A 1x1 transparent PNG.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64');
