// A stand-in Studio for the studio-op tests. Every JSON answer carries the
// Authorization header the request arrived with, so a command that prints a
// response unredacted would leak the token.
import http from 'node:http';

export function fakeToken(prefix = 'api') {
  // Assembled at runtime: never a provider-shaped literal in source.
  const id = Array.from({ length: 32 }, (_, index) => '0123456789abcdef'[(index * 7 + 3) % 16]).join('');
  const secret = Buffer.from(String(Math.random()) + String(Date.now())).toString('base64url').padEnd(43, 'q');
  return [prefix, id, secret].join('.');
}

export function remoteAttempt(overrides = {}) {
  return {
    plane: 'Remote', attemptId: 'review-1', receivedAt: '2026-11-05T10:00:00Z', outcome: 'Pass',
    buildTestsResult: 'passed', aspects: [], subjectSha: 'a'.repeat(40), reportRef: 'remote-review-grade-review-1.md',
    ...overrides,
  };
}

export function reviewProjection(overrides = {}) {
  const attempts = overrides.attempts ?? [remoteAttempt()];
  return {
    attempts, rounds: attempts.length, latestOutcome: attempts[0]?.outcome ?? null,
    blockingAspects: [], delivery: { status: 'not-attempted' }, decisionRequired: { required: false },
    ...overrides,
  };
}

export function card(key, overrides = {}) {
  return {
    id: key.toLowerCase(), taskKey: `/demo/app::${key.toLowerCase()}`, key, title: `Card ${key}`, state: '5-human-review', kind: 'task',
    projectName: 'Demo App', watchPath: '/demo/app', parkedBlocker: null, needsInput: null,
    orchestratorVerdict: null, steerPendingSince: null, ...overrides,
  };
}

/**
 * Starts the fake. `cards` maps a card key to { card, projection }. Returns
 * helpers to push decision events to open streams and to inspect requests.
 */
export async function startFakeStudio({ cards = {}, preflightBlockers = [] } = {}) {
  const requests = [];
  const streams = new Set();
  const events = [];
  let lease = null;
  let leaseCounter = 0;
  let revision = 1;
  const log = [];
  // Magazine articles by "scope/slug": the same revision rules as Studio.
  const articles = new Map();
  // Keys whose next read is followed by another writer's revision.
  const articleRaces = new Set();

  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      const url = new URL(req.url, 'http://fake');
      const authorization = req.headers.authorization ?? null;
      const parsed = body ? safeJson(body) : null;
      requests.push({ method: req.method, path: url.pathname, query: Object.fromEntries(url.searchParams),
        authorization, ifMatch: req.headers['if-match'] ?? null, clientId: req.headers['x-client-id'] ?? null,
        body: parsed });
      const send = (status, payload) => {
        res.writeHead(status, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(Array.isArray(payload)
          ? payload.map(item => ({ ...item, echo: authorization }))
          : { ...payload, echo: authorization }));
      };
      route(req, url, parsed, send, res);
    });
  });

  function route(req, url, body, send, res) {
    const path = url.pathname;
    const method = req.method;
    const cardMatch = path.match(/^\/api\/tasks\/([^/]+)(\/.*)?$/);
    if (method === 'GET' && path === '/api/watch-paths')
      return send(200, [{ name: 'Demo App', path: '/demo/app' }]);
    if (method === 'GET' && path === '/api/operator/decisions')
      return send(200, { events: [], cursor: '0', headCursor: String(events.length) });
    if (method === 'GET' && path === '/api/operator/decisions/stream') {
      res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
      res.write('retry: 5000\n\n');
      const after = Number(url.searchParams.get('after') ?? events.length);
      for (const event of events.filter(item => Number(item.cursor) > after)) writeEvent(res, event);
      streams.add(res);
      res.on('close', () => streams.delete(res));
      return;
    }
    if (path === '/api/operator/watcher/lease' && method === 'POST') {
      if (lease && !body?.takeOver)
        return send(409, { error: 'watcher-active', holder: lease.holder, session: lease.session,
          lastHeartbeatUtc: '2026-11-05T10:00:00Z', message: 'Another watcher holds the lease.' });
      lease = { leaseId: `lease-${++leaseCounter}`, holder: 'service-account:demo', session: body?.session ?? null,
        heartbeatSeconds: 1 };
      return send(200, { outcome: 'granted', lease });
    }
    const heartbeat = path.match(/^\/api\/operator\/watcher\/lease\/([^/]+)\/heartbeat$/);
    if (heartbeat && method === 'POST')
      return lease?.leaseId === heartbeat[1] ? send(200, { lease }) : send(409, { error: 'watcher-lease-lost' });
    const release = path.match(/^\/api\/operator\/watcher\/lease\/([^/]+)$/);
    if (release && method === 'DELETE') {
      if (lease?.leaseId === release[1]) lease = null;
      res.writeHead(204); res.end(); return;
    }
    if (method === 'GET' && path === '/api/operator/batch-triage')
      return send(200, { generatedAtUtc: '2026-11-05T10:00:00Z', pendingPushAlertMinutes: 3, projects: [{
        project: 'Demo App', batchGateEnabled: true, plan: 'wait', flights: [], members: [],
        redMembers: [], isolatingMembers: [], pendingPushes: [] }] });
    if (method === 'GET' && path === '/api/operator/backups')
      return send(200, { state: 'failing', running: false, lastGoodAgeHours: 13,
        lastGood: { id: 'studio-backup-20261105T000000Z-abc123', createdAt: '2026-11-05T00:00:00+00:00', sizeBytes: 1024 },
        lastVerification: { at: '2026-11-05T00:05:00+00:00', outcome: 'verified' },
        offHost: { kind: 'sftp', state: 'current', listing: { state: 'not-listable', archiveCount: 0, notInCatalog: [] } },
        lastRestore: { backupId: 'studio-backup-20261104T000000Z-def456', archiveCreatedAt: '2026-11-04T00:00:00+00:00',
          restoredAt: '2026-11-04T09:00:00+00:00', restoredBy: 'owner@host-b' },
        warnings: [{ code: 'backup-stale', severity: 'danger', message: 'The newest good backup is 13 hours old.' }] });
    if (method === 'GET' && path === '/api/operator/capacity')
      return send(200, { quota: [], activeFallbacks: [], waitingCards: [], modelRoutingPins: [] });
    if (method === 'GET' && path === '/api/security/posture')
      return send(200, { generatedAt: '2026-11-09T12:00:00Z',
        summary: { total: 2, critical: 1, high: 0, medium: 1, low: 0, info: 0, actionSoon: 1 },
        findings: [
          { id: 'second-factor:u1', rank: 1, score: 95, severity: 'critical', area: 'second-factor', title: 'Owner has no second factor',
            detail: 'Enroll TOTP or a passkey.', actionSoon: true, dueAt: null, subject: 'owner' },
          { id: 'credentials:token:t1:no-expiry', rank: 2, score: 50, severity: 'medium', area: 'credentials', title: 'API token ci never expires',
            detail: 'Replace it with one that expires.', actionSoon: false, dueAt: null, subject: 'ci' },
        ] });
    if (method === 'GET' && path === '/api/model-selection/policy')
      return send(200, { consumers: { review: { primary: { cli: 'codex', model: 'demo-model' } } } });
    if (method === 'PUT' && /^\/api\/projects\/[^/]+\/pipeline-step$/.test(path))
      return send(200, { stepId: body?.stepId, model: body?.model });
    if (method === 'GET' && /^\/api\/operator\/preflight\/(restart|deploy)$/.test(path))
      return preflightBlockers.length
        ? send(409, { operation: path.split('/').pop(), allowed: false, blockers: preflightBlockers })
        : send(200, { operation: path.split('/').pop(), allowed: true, blockers: [] });
    if (method === 'POST' && path === '/api/operator/deploy/plan')
      return send(200, { createdAtUtc: '2026-11-05T10:00:00Z', expiresAtUtc: '2026-11-05T10:30:00Z',
        targetVersion: body?.targetVersion, steps: [
          { order: 1, kind: 'drain' }, { order: 2, kind: 'task-server' }, { order: 3, kind: 'readiness' },
          { order: 4, kind: 'engine' }, { order: 5, kind: 'hosts' }] });
    if (method === 'POST' && path === '/api/operator/log') {
      const entry = { cursor: log.length + 1, ...body };
      log.push(entry);
      return send(200, entry);
    }
    if (method === 'GET' && path === '/api/operator/log')
      return send(200, { entries: log, cursor: log.length });
    const article = path.match(/^\/api\/magazine\/([^/]+)\/([^/]+)$/);
    if (article) return magazine(req, method, decodeURIComponent(article[1]), decodeURIComponent(article[2]), body, send);
    if (method === 'POST' && /^\/api\/projects\/[^/]+\/batch-gate\/members\/[^/]+\/eject$/.test(path))
      return send(202, { accepted: true });
    if (method === 'POST' && (path === '/api/tasks' || path === '/api/tasks/'))
      return send(200, { id: 'demo-created', taskKey: 'DEMO-90', title: body?.title });
    if (cardMatch) {
      const entry = cards[decodeURIComponent(cardMatch[1])];
      const rest = cardMatch[2] ?? '';
      if (!entry) return send(404, { error: 'not-found' });
      if (method === 'GET' && rest === '' && entry.warming > 0) {
        entry.warming -= 1;
        return send(202, { state: 'warming', reason: 'task-index-warming' });
      }
      if (method === 'GET' && rest === '') return send(200, { info: entry.card, promptMarkdown: '' });
      if (method === 'GET' && rest === '/review-projection') return send(200, entry.projection ?? reviewProjection({ attempts: [] }));
      if (method === 'POST' && rest === '/attention')
        return send(200, { lease: { holder: 'service-account:demo' }, revision: `"r${revision}"` });
      if (req.headers['if-match'] !== `"r${revision}"`) return send(412, { error: 'stale-card-revision' });
      revision += 1;
      if (method === 'POST' && rest === '/move' && body?.targetState === '6-completed' && entry.contentGap)
        return send(409, { error: 'Completion refused: no content verdict (requirement-fit).', code: 'completion-content-gap' });
      if (method === 'POST' && rest === '/move') { entry.card.state = body?.targetState; return send(200, { moved: true }); }
      if (method === 'POST' && rest === '/continue')
        return send(202, { status: 'queued', base: { ref: 'refs/heads/agent-studio/results/run_demo/fence-1/abc', sha: 'b'.repeat(40), source: 'latest-result' } });
      if (method === 'PUT' && rest === '/title') { entry.card.title = body?.title; return send(200, { title: body?.title }); }
    }
    return send(404, { error: 'not-found' });
  }

  function magazine(req, method, scope, slug, body, send) {
    if (!/^(installation|(workspace|project):[A-Za-z0-9._-]+)$/.test(scope))
      return send(400, { error: 'magazine-scope-invalid' });
    if (scope === 'project:FOREIGN') return send(403, { error: 'project-scope-denied' });
    const key = `${scope}/${slug}`;
    const existing = articles.get(key);
    if (method === 'GET') {
      if (!existing) return send(404, { error: 'magazine-article-not-found' });
      const latest = existing.at(-1);
      send(200, { ...latest, currentRevision: latest.revision });
      if (articleRaces.delete(key)) existing.push({ ...latest, revision: latest.revision + 1 });
      return;
    }
    if (method !== 'PUT') return send(405, { error: 'method-not-allowed' });
    if (Buffer.byteLength(body?.body ?? '', 'utf8') > 256 * 1024) return send(413, { error: 'magazine-body-too-large' });
    const header = req.headers['if-match'];
    const expected = header === undefined ? null : Number(String(header).replace(/^W\//, '').replace(/"/g, ''));
    const current = existing?.at(-1).revision ?? 0;
    if (existing && expected === null) return send(428, { error: 'if-match-required', currentRevision: current });
    if (expected !== null && expected !== current)
      return send(409, { error: 'revision-conflict', message: `Revision ${expected} is stale; the current revision is ${current}.`, currentRevision: current });
    const next = { scope, slug, revision: current + 1, ...body };
    articles.set(key, [...(existing ?? []), next]);
    return send(existing ? 200 : 201, { ...next, currentRevision: next.revision });
  }

  function writeEvent(res, event) {
    res.write(`id: ${event.cursor}\nevent: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`);
  }

  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return {
    url: `http://127.0.0.1:${server.address().port}`,
    requests,
    articles,
    articleRaces,
    get lease() { return lease; },
    publish(type, overrides = {}) {
      const event = { id: `evt-${events.length + 1}`, cursor: String(events.length + 1), type,
        project: 'Demo App', cardKey: 'DEMO-1', reason: `Reason for ${type}`, suggestedOperations: [],
        evidence: [], ...overrides };
      events.push(event);
      for (const stream of streams) writeEvent(stream, event);
      return event;
    },
    get openStreams() { return streams.size; },
    close: () => new Promise(resolve => {
      for (const stream of streams) stream.destroy();
      server.closeAllConnections?.();
      server.close(() => resolve());
    }),
  };
}

function safeJson(text) {
  try { return JSON.parse(text); } catch { return text; }
}
