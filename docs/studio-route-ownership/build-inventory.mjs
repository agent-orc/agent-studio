#!/usr/bin/env node

import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const dossier = import.meta.dirname;
const root = path.resolve(dossier, '../..');
const candidatePayload = JSON.parse(execFileSync(
  process.execPath,
  [path.join(dossier, 'extract-routes.mjs')],
  { cwd: root, encoding: 'utf8' },
));

const extras = [
  ['SSE', '/api/devtools/update-stable/stream', 'frontend/src/app/features/dev-tools/components/update-stable-console/update-stable-console.component.ts:62'],
  ['POST', '/api/clients/{clientId}/drain', 'frontend/src/app/features/remote-hosts/services/remote-hosts.service.ts:350'],
  ['POST', '/api/clients/{clientId}/retire', 'frontend/src/app/features/remote-hosts/services/remote-hosts.service.ts:355'],
  ['POST', '/api/clients/{clientId}/revive', 'frontend/src/app/features/remote-hosts/services/remote-hosts.service.ts:359'],
  ['GET', '/api/projects/{project}/proposals/evidence/{path*}', 'frontend/src/app/features/project-detail/components/project-proposals-panel/project-proposals-panel.component.ts:140'],
  ['GET', '/api/projects/{project}/wiki/assets/{path*}', 'frontend/src/app/services/project-docs.service.ts:237'],
  ['GET', '/api/runner/{project}/orchestrator-chat/attachments/{fileName}', 'frontend/src/app/features/orchestrator/components/orchestrator-side-sheet/orchestrator-side-sheet.util.ts:314'],
  ['GET', '/api/tasks/{taskId}/attachments/{fileName}', 'frontend/src/app/features/task-detail/components/protocol-pane/protocol-image-resolver.ts:34'],
  ['GET', '/api/tasks/{taskId}/results/{path*}', 'frontend/src/app/features/task-detail/components/task-artifact-links/task-artifact-link.ts:47'],
  ['GET', '/api/tasks/{taskId}/screenshot', 'frontend/src/app/features/task-detail/components/protocol-pane/protocol-image-resolver.ts:44'],
];

function canonicalPath(input) {
  let value = input
    .replace(/\(watchPath \?.*$/, '')
    .replaceAll('{job.id}', '{taskId}')
    .replaceAll('{jobId}', '{taskId}')
    .replaceAll('{task}', '{taskId}')
    .replaceAll('{projectName}', '{project}')
    .replaceAll('{projId}', '{projectId}')
    .replaceAll('{current.clientId}', '{clientId}')
    .replaceAll('{host.clientId}', '{clientId}')
    .replaceAll('{CLIENT_ID}', '{clientId}')
    .replaceAll('{proposalId}', '{proposalId}')
    .replaceAll('{proposal.id}', '{proposalId}')
    .replaceAll('{value}', '{path*}')
    .replaceAll('{fileName}/history', '{path*}/history')
    .replaceAll('/steering/read-analytics{suffix}', '/steering/read-analytics')
    .replace('/api/clients/', '/api/clients/');
  if (value.endsWith('/') && value !== '/api/adhoc-usage/') value = value.slice(0, -1);
  return value;
}

function splitQuery(input) {
  const braceQuery = input.match(/\{\?([^}]+)\}/);
  if (braceQuery) {
    return {
      path: input.replace(braceQuery[0], ''),
      fixed: {},
      optional: braceQuery[1].split(','),
    };
  }
  const question = input.indexOf('?');
  if (question < 0) return { path: input, fixed: {}, optional: [] };
  const raw = input.slice(question + 1);
  const fixed = {};
  const optional = [];
  for (const part of raw.split('&')) {
    if (!part) continue;
    if (part === '{params}') {
      optional.push('watchPath');
      continue;
    }
    const [key, val = ''] = part.split('=', 2);
    if (/^\{.+\}$/.test(val)) optional.push(key);
    else fixed[key] = val;
  }
  return { path: input.slice(0, question), fixed, optional };
}

const queryKeys = new Map(Object.entries({
  'GET /api/search': ['q', 'domains', 'limit'],
  'GET /api/projects': ['includeArchived'],
  'GET /api/workspaces': ['includeArchived'],
  'GET /api/tasks/archive': ['project', 'watchPath', 'offset', 'limit', 'search'],
  'GET /api/tasks/{taskId}': ['project', 'watchPath'],
  'GET /api/epics': ['includeFixtures', 'status', 'project'],
  'GET /api/epics/completed/count': ['includeFixtures', 'project'],
  'GET /api/projects/pipeline-catalogue': ['projectName', 'pipelineType'],
  'GET /api/projects/{project}/cycle-time': ['window', 'detail'],
  'GET /api/projects/{project}/visual-evidence': ['refresh'],
  'GET /api/projects/{project}/token-usage/heatmap': ['days'],
  'GET /api/projects/{project}/token-usage/expensive': ['limit'],
  'GET /api/projects/{project}/token-usage/pipeline-cost': ['days'],
  'GET /api/projects/{project}/wiki/search': ['q', 'semantic', 'limit'],
  'GET /api/projects/{project}/workbenches': ['history'],
  'GET /api/projects/{project}/steering/read-analytics': ['days'],
  'DELETE /api/projects/{project}/proposals': ['keepGeneration'],
  'GET /api/workbenches': ['project'],
  'GET /api/runtime/{project}/events': ['refresh'],
  'GET /api/bus/{project}/recent': ['limit'],
  'GET /api/bus/{project}/messages': ['jobId', 'runId', 'participantId', 'kind', 'severity', 'cli', 'skill', 'tag', 'correlationId', 'since', 'until', 'limit'],
  'GET /api/bus/{project}/token-aggregate': ['since', 'until'],
  'GET /api/workspace/screenshots': ['windowHours', 'projectFilter'],
  'GET /api/workspace/summary': ['windowHours'],
  'GET /api/workspace/tokens/timeline': ['windowHours', 'bucketMinutes'],
  'GET /api/workspace/tokens/timeline/cached': ['windowHours', 'bucketMinutes'],
  'GET /api/workspace/tokens/expensive-jobs': ['limit'],
  'GET /api/cli/model-routing/recommendation': ['taskType', 'cliType'],
  'GET /api/clients/{clientId}/telemetry': ['window'],
  'GET /api/cli/{cliType}/models': ['refresh'],
  'GET /api/git/hygiene': ['project'],
  'GET /api/git/inventory': ['project'],
  'GET /api/git/history': ['project', 'offset', 'limit'],
  'GET /api/git/integration': ['project'],
  'GET /api/git/project-commit/files': ['project', 'sha'],
  'GET /api/git/project-commit/diff': ['project', 'sha', 'path'],
  'GET /api/git/cleanup/plan': ['project'],
  'POST /api/git/cleanup/execute': ['project'],
}));

function inferredQueryKeys(method, routePath) {
  const explicit = queryKeys.get(`${method} ${routePath}`) ?? [];
  const keys = [...explicit];
  const add = (key) => { if (!keys.includes(key)) keys.push(key); };
  if (/^\/api\/tasks\/\{taskId\}\//.test(routePath)
      && !routePath.includes('/attachments/{fileName}')) add('watchPath');
  if (/\/tasks\/\{taskId\}\/(git\/diff|git\/file|commit\/diff|commits\/.*\/(diff|file)|commits\/diff)$/.test(routePath)) add('path');
  if (/\/tasks\/\{taskId\}\/files\/\{path\*\}/.test(routePath)) {
    add('scope');
    if (!routePath.endsWith('/history')) add('at');
  }
  if (routePath === '/api/tasks/{taskId}/runs/{runIndex}/diff' || routePath === '/api/tasks/{taskId}/screenshot') add('path');
  if (routePath === '/api/tasks/{taskId}/dependents') add('kind');
  if (routePath === '/api/tasks/{taskId}/stop') add('reason');
  if (/^\/api\/epics\/\{epicId\}/.test(routePath)) add('watchPath');
  if (/\/tasks\/\{taskId\}\/(attachments|results)\//.test(routePath) || routePath.endsWith('/screenshot')) add('watchPath');
  return keys;
}

function classification(routePath, method) {
  if (/^\/api\/projects\/\{project\}\/wiki\/grading\//.test(routePath)) return 'task-server';
  const devSeat = [
    /^\/api\/environment$/,
    /^\/api\/devtools\//,
    /^\/api\/git\//,
    /^\/api\/settings\/cli(?:\/|$)/,
    /^\/api\/concept-docs\//,
    /^\/api\/adhoc-usage\/$/,
    /^\/api\/projects\/\{projectId\}\/url-suggestions$/,
    /^\/api\/projects\/\{projectId\}\/urls\/(?:test|\{urlId\}\/(?:context|readiness|diagnostic|process|start))$/,
    /^\/api\/projects\/\{project\}\/(?:wiki|architecture|graph|steering|style-guides|build-profile)(?:\/|$)/,
    /^\/api\/projects\/\{project\}\/security(?:\/files|\/meta|$)/,
    /^\/api\/(?:workbenches|projects\/\{project\}\/workbenches)(?:\/|$)/,
    /^\/api\/tasks\/\{taskId\}\/(?:git|commit|commits|provenance|open-in-vscode)(?:\/|$)/,
  ];
  if (devSeat.some((pattern) => pattern.test(routePath))) return 'dev-seat';
  if (/^\/api\/cli\//.test(routePath)) {
    const centralPolicy = /\/(?:caps|wait-policy|model-routes|model-routing\/policy|model-routing\/recommendation|model-routing\/economy-mode)$/.test(routePath);
    return centralPolicy ? 'task-server' : 'dev-seat';
  }
  if (/^\/api\/projects\/\{project\}\/skills?$/.test(routePath)
      || /^\/api\/projects\/\{project\}\/skill-readiness$/.test(routePath)) return 'dev-seat';
  if (method === 'SSE' && routePath.startsWith('/api/devtools/')) return 'dev-seat';
  return 'task-server';
}

const existingV1 = new Set([
  'GET /api/v1/management/status',
  'GET /api/v1/management/remote-hosts',
  'PUT /api/v1/hosts/{hostId}/runtime-capacity',
  'PUT /api/v1/hosts/{hostId}/project-policy',
]);

function targetRoute(method, routePath, routeClass) {
  if (routeClass === 'dev-seat') return routePath;
  if (existingV1.has(`${method} ${routePath}`)) return routePath;
  if (routePath.startsWith('/api/v1/')) return routePath;
  if (routePath === '/hubs/jobs') return '/hubs/v1/studio';
  if (routePath === '/api/workspaces') return '/api/v1/workspaces';
  if (routePath === '/api/projects') return '/api/v1/projects';
  if (routePath === '/api/tasks/grouped') return '/api/v1/studio/board';
  if (routePath.startsWith('/api/tasks/{taskId}')) {
    return routePath.replace('/api/tasks/{taskId}', '/api/v1/projects/{projectId}/tasks/{taskId}');
  }
  if (routePath.startsWith('/api/')) return routePath.replace('/api/', '/api/v1/studio/');
  return routePath;
}

function frontendOwner(evidence) {
  const first = evidence[0] ?? '';
  if (first.includes('/features/board/')) return 'Board';
  if (first.includes('/features/task-detail/')) return 'Task detail';
  if (first.includes('/features/project-detail/')) return 'Project hub';
  if (first.includes('/features/remote-hosts/')) return 'Remote hosts';
  if (first.includes('/features/tokens/') || first.includes('/features/quota/')) return 'Usage and quota';
  if (first.includes('/features/orchestrator/')) return 'Orchestrator';
  if (first.includes('/features/studio-shell/')) return 'Studio shell';
  if (first.includes('/features/dev-tools/')) return 'Developer tools';
  return 'Shared frontend services';
}

function priority(routePath, method, routeClass, status) {
  if (routeClass !== 'task-server' || status === 'exists') return null;
  if (/^\/api\/(auth|tasks(?:\/grouped|\/\{taskId\}(?:$|\/state|\/move|\/start|\/stop|\/continue))|projects$|workspaces$|runner\/(?:status|\{project\}\/orchestrator-chat)|orchestrator)|^\/hubs\//.test(routePath)) return 'P0';
  if (/^\/api\/(tasks|epics|tags|clients|v1|runner|projects\/\{project\}\/(?:pipeline|execution|autonomy)|workspace)/.test(routePath)) return 'P1';
  if (/^\/api\/(bus|runtime|analysis|drift|supervisor|pipeline|crash-recovery|projects\/\{project\}\/(?:cycle-time|token-usage|throughput|visual-evidence|deployment|test-runs|regression-radar|snapshot|security|design))/.test(routePath)) return 'P2';
  return method === 'GET' ? 'P3' : 'P2';
}

function estimate(method, routePath, routeClass, status) {
  if (routeClass !== 'task-server' || status === 'exists') return null;
  let points = 1;
  if (method === 'WS') points = 8;
  else if (/\/auth\//.test(routePath)) points = 3;
  else if (/attachments|artifacts|results|screenshot|thumbnail/.test(routePath)) points = method === 'GET' ? 2 : 3;
  else if (method !== 'GET') points = 2;
  else if (/orchestrator|runner|management|search/.test(routePath)) points = 2;
  const size = points <= 1 ? 'S' : points <= 2 ? 'M' : points <= 3 ? 'L' : 'XL';
  return { size, weightedRoutePoints: points, basis: `${method} contract plus authorization, compatibility, and route-level tests` };
}

function ownershipCaveat(routePath, method) {
  if (method === 'GET' && routePath === '/api/tasks/{taskId}/files/{path*}') {
    return 'The default task-file read moves to the Task Server. The current scope=code variant crosses into a repository checkout and must be split onto a new dev-seat route before cutover.';
  }
  if (method === 'GET' && routePath === '/api/search') {
    return 'Task results remain authoritative on the Task Server. Commit and repository-file search must become a separately named dev-seat query or a server index fed by Runner evidence.';
  }
  if (method === 'GET' && routePath === '/api/projects/{project}/snapshot') {
    return 'The Task Server owns the project and queue projection. Checkout paths and local repository status must be removed or joined from a separately fetched dev-seat projection.';
  }
  return null;
}

const merged = new Map();
for (const candidate of [...candidatePayload.routes, ...extras.map(([method, route, evidence]) => ({ method, path: route, evidence: [evidence] }))]) {
  const query = splitQuery(canonicalPath(candidate.path));
  let routePath = canonicalPath(query.path);
  if (candidate.method === 'GET' && routePath === '/api/tasks/{taskId}/files/{fileName}') {
    routePath = '/api/tasks/{taskId}/files/{path*}';
  }
  const key = `${candidate.method} ${routePath}`;
  const current = merged.get(key) ?? { method: candidate.method, path: routePath, evidence: [], fixed: {}, optional: [] };
  for (const item of Array.isArray(candidate.evidence) ? candidate.evidence : [candidate.evidence]) {
    if (!current.evidence.includes(item)) current.evidence.push(item);
  }
  Object.assign(current.fixed, query.fixed);
  for (const item of [...query.optional, ...inferredQueryKeys(candidate.method, routePath)]) {
    if (item && !current.optional.includes(item)) current.optional.push(item);
  }
  merged.set(key, current);
}

const frontendRoutes = [...merged.values()]
  .sort((a, b) => a.path.localeCompare(b.path) || a.method.localeCompare(b.method))
  .map((route, index) => {
    const routeClass = classification(route.path, route.method);
    const status = routeClass === 'dev-seat' ? 'not-applicable' : existingV1.has(`${route.method} ${route.path}`) ? 'exists' : 'must-add';
    const move = estimate(route.method, route.path, routeClass, status);
    const optional = [...route.optional].sort();
    const queryVariants = [{ kind: 'none', query: '' }];
    if (Object.keys(route.fixed).length) queryVariants.push({ kind: 'fixed', values: route.fixed });
    if (optional.length) queryVariants.push({ kind: 'optional-subset', keys: optional });
    const localReason = 'The route directly reads or controls the Windows development seat: repository files, Git, local CLI capability, launcher, or developer tooling.';
    const serverReason = 'The route exposes durable task, orchestration, identity, event, policy, artifact, or management state assigned to the Task Server by the target architecture.';
    return {
      id: `fe-${String(index + 1).padStart(3, '0')}`,
      method: route.method,
      path: route.path,
      queryVariants,
      classification: routeClass,
      owningComponent: routeClass === 'dev-seat' ? 'Windows connector dev-seat module' : 'Remote Task Server',
      frontendOwner: frontendOwner(route.evidence),
      v1Status: status,
      targetRoute: targetRoute(route.method, route.path, routeClass),
      frontendEvidence: route.evidence.sort(),
      decisionEvidence: routeClass === 'dev-seat'
        ? ['docs/operations/remote-task-server-local-studio.md:99-106', 'docs/concepts/distributed-agent-studio-target-architecture.md:110-115']
        : ['docs/concepts/distributed-agent-studio-target-architecture.md:17-23', 'docs/concepts/distributed-agent-studio-target-architecture.md:110-115'],
      classificationReason: routeClass === 'dev-seat' ? localReason : serverReason,
      ownershipCaveat: ownershipCaveat(route.path, route.method),
      d4bPriority: priority(route.path, route.method, routeClass, status),
      estimate: move,
    };
  });

const groupMatches = [];
for (const file of walk(path.join(root, 'backend')).filter((item) => item.endsWith('.cs')).sort()) {
  const source = fs.readFileSync(file, 'utf8');
  const pattern = /MapGroup\(\s*"(\/api[^"]*)"/g;
  let match;
  while ((match = pattern.exec(source))) {
    const line = source.slice(0, match.index).split('\n').length;
    groupMatches.push({ path: match[1], evidence: `${path.relative(root, file).replaceAll(path.sep, '/')}:${line}` });
  }
}
const orchestratorApiGroups = groupMatches
  .sort((a, b) => a.path.localeCompare(b.path))
  .map((group, index) => {
    const matchingRoutes = frontendRoutes.filter((route) => route.path === group.path || route.path.startsWith(`${group.path}/`));
    const classes = [...new Set(matchingRoutes.map((route) => route.classification))];
    return {
      id: `group-${String(index + 1).padStart(2, '0')}`,
      path: group.path,
      frontendOperationCount: matchingRoutes.length,
      routeOwnership: classes.length > 1 ? 'mixed' : classes[0] ?? 'non-studio',
      evidence: group.evidence,
    };
  });

function bundleFor(route) {
  if (route.d4bPriority === 'P0') return 'core-attach';
  if (route.d4bPriority === 'P1') return 'task-detail-and-hosts';
  if (route.d4bPriority === 'P2') return 'operations-and-insight';
  return 'administration-and-tail';
}
const moveRoutes = frontendRoutes.filter((route) => route.v1Status === 'must-add');
const bundleMeta = {
  'core-attach': { order: 1, title: 'Core attach path', impact: 'Login, board, task mutations, orchestration chat, runner state, and live updates.' },
  'task-detail-and-hosts': { order: 2, title: 'Task detail and host control', impact: 'Task history, artifacts, pipeline detail, host policy, and workspace projections.' },
  'operations-and-insight': { order: 3, title: 'Operations and insight', impact: 'Runtime, bus, cycle time, token, deployment, security, and supervisor projections.' },
  'administration-and-tail': { order: 4, title: 'Administration and long tail', impact: 'Admin configuration and lower-frequency Studio surfaces.' },
};
const bundles = Object.entries(bundleMeta).map(([id, meta]) => {
  const routes = moveRoutes.filter((route) => bundleFor(route) === id);
  return {
    id,
    ...meta,
    routeCount: routes.length,
    weightedRoutePoints: routes.reduce((sum, route) => sum + route.estimate.weightedRoutePoints, 0),
    routeIds: routes.map((route) => route.id),
  };
});

const countsByClass = Object.fromEntries(['task-server', 'dev-seat', 'retired'].map((name) => [name, frontendRoutes.filter((route) => route.classification === name).length]));
const weightedRoutePoints = moveRoutes.reduce((sum, route) => sum + route.estimate.weightedRoutePoints, 0);
const routeDays = Math.ceil(weightedRoutePoints / 6);
const sharedFoundationDays = 15;
const baselineDays = routeDays + sharedFoundationDays;

const payload = {
  schemaVersion: 1,
  id: 'studio-route-ownership-2026-09-07',
  title: 'Studio route ownership inventory',
  updatedAt: '2026-09-07',
  sourceTaskKeys: ['AGT-2731'],
  countingUnit: 'A route is one distinct frontend method plus normalized path template. Query combinations are variants on that route, not additional routes. WS and SSE transports are counted once each.',
  scope: {
    frontend: 'frontend/src/app/**/*.ts excluding *.spec.ts; HttpClient, Fetch upload, EventSource, URL-producing media helpers, and SignalR.',
    backendGroups: 'Every app.MapGroup("/api/...") call under backend/, representing OrchestratorApi endpoint groups.',
    exclusions: ['Comments and models without a runtime call site', 'The cross-origin UpdateService /update surface on port 5039', 'Runner-only and management-only individual routes not called by Angular'],
  },
  counts: {
    frontendOperations: frontendRoutes.length,
    frontendApiOperations: frontendRoutes.filter((route) => route.path.startsWith('/api')).length,
    frontendHubOperations: frontendRoutes.filter((route) => route.path.startsWith('/hubs')).length,
    byClassification: countsByClass,
    orchestratorApiMapGroups: orchestratorApiGroups.length,
    frontendV1Operations: frontendRoutes.filter((route) => route.path.startsWith('/api/v1/')).length,
    frontendV1OperationsExistingInStandaloneTaskServer: frontendRoutes.filter((route) => route.v1Status === 'exists').length,
    d4bRoutesToAdd: moveRoutes.length,
  },
  d4bEstimate: {
    method: 'Each must-add route is sized S=1, M=2, L=3, XL=8 weighted route points. Six points equal one engineering day after shared foundations. Add 15 days for connector security, version negotiation, replayable WebSocket infrastructure, contract harnesses, and cutover controls.',
    weightedRoutePoints,
    routeImplementationDays: routeDays,
    sharedFoundationDays,
    baselineEngineeringDays: baselineDays,
    planningRangeEngineeringDays: [Math.ceil(baselineDays * 0.8), Math.ceil(baselineDays * 1.3)],
    bundles,
  },
  frontendRoutes,
  orchestratorApiGroups,
};

const output = `${JSON.stringify(payload, null, 2)}\n`;
if (process.argv.includes('--write')) fs.writeFileSync(path.join(dossier, 'routes.json'), output);
else process.stdout.write(output);

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(target) : entry.isFile() ? [target] : [];
  });
}
