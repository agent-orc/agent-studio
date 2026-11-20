import type { StructureData, StructureLink, StructureNode } from './structure.model';

// Role-named structure fixtures shared by the unit tests and the Playwright
// spec. They name roles only: no machine names, addresses or accounts.

const node = (id: string, kind: string, label: string, parentId: string | null,
  extra: Partial<StructureNode> = {}): StructureNode => ({ id, kind, label, parentId, unitId: null,
  environmentId: null, project: null, provenance: 'declared', sourceRef: 'role fixture', ...extra });
const link = (from: string, to: string, kind: string, label: string): StructureLink =>
  ({ from, to, kind, label, sourceRef: 'role fixture' });

export const agtFixture: StructureData = {
  nodes: [
    node('b', 'boundary', 'AGT installation', null),
    node('cloud', 'location', 'Cloud provider', 'b'),
    node('site', 'location', 'Operator site', 'b'),
    node('home', 'location', 'Home lab', 'b'),
    node('studio', 'physical', 'Studio machine', 'site'),
    node('runner', 'cloud-server', 'Linux runner host', 'cloud'),
    node('lab', 'physical', 'Home-lab physical machine', 'home'),
    node('vm', 'vm', 'Home-lab VM', 'lab'),
    node('api', 'process', 'Studio backend', 'studio', { role: 'control' }),
    node('update', 'process', 'Update service', 'studio', { role: 'control' }),
    node('coding', 'process', 'Coding host', 'runner', { role: 'execution' }),
    node('engine', 'container', 'Orchestrator Engine', 'runner', { role: 'control' }),
    ...[1, 2, 3].map(n => node(`review-${n}`, 'container', `Review host ${n}`, 'vm', { role: 'execution' })),
    node('gate', 'container', 'Build and test gate', 'vm', { role: 'verification' }),
    node('github', 'external', 'GitHub', null),
    node('registry', 'external', 'Container registry', null),
    node('models', 'external', 'Model providers', null),
  ],
  links: [
    link('api', 'coding', 'tunnel', 'Runner route'),
    link('api', 'review-1', 'tunnel', 'Gate route'),
    link('coding', 'github', 'call', 'Git'),
    link('registry', 'engine', 'artifact', 'image pull'),
    link('review-1', 'models', 'call', 'model calls'),
    link('gate', 'update', 'artifact', 'release package'),
  ],
};

const system = 'system:product:production';
export const workspaceFixture: StructureData = {
  nodes: [
    node('installation', 'boundary', 'Workspace installation', null),
    node(system, 'system', 'Product · production', 'installation', { environmentId: 'production' }),
    node('machine:web', 'vm', 'Web placement', 'installation'),
    node('machine:runner', 'cloud-server', 'Runner placement', 'installation'),
    node('machine:studio', 'physical', 'Studio placement', 'installation'),
    node('website', 'process', 'Website process', 'machine:web',
      { project: 'PROJ-002', ownerLabel: 'Website', systemBoundaryId: system, unitId: 'website', role: 'web' }),
    node('docs', 'container', 'Docs site', 'machine:web',
      { project: 'PROJ-002', ownerLabel: 'Website', systemBoundaryId: system, unitId: 'docs', role: 'web' }),
    node('agent', 'process', 'Run-target agent', 'machine:web',
      { project: 'PROJ-001', ownerLabel: 'AGT', systemBoundaryId: system, unitId: 'agent', role: 'deploy' }),
    node('engine', 'container', 'Orchestrator Engine', 'machine:runner', { project: 'PROJ-001', ownerLabel: 'AGT', role: 'control' }),
    node('coding', 'process', 'Coding host', 'machine:runner', { project: 'PROJ-001', ownerLabel: 'AGT', role: 'execution' }),
    node('channel', 'registry', 'npm channel', 'machine:runner', { project: 'PROJ-003', ownerLabel: 'Library', role: 'package' }),
    node('api', 'process', 'Studio backend', 'machine:studio', { project: 'PROJ-001', ownerLabel: 'AGT', role: 'control' }),
    node('github', 'external', 'GitHub', null),
    node('npm', 'external', 'Public package registry', null),
    node('images', 'external', 'Container registry', null),
  ],
  links: [
    link('api', 'agent', 'call', 'desired state'),
    link('api', 'coding', 'tunnel', 'Runner route'),
    link('website', 'channel', 'artifact', 'Package'),
    link('channel', 'npm', 'artifact', 'publish'),
    link('coding', 'github', 'call', 'Git'),
    link('images', 'docs', 'artifact', 'image pull'),
  ],
};

/** Sixty services in five namespaces, run in two production regions, staging and development. */
export const complexServices: Readonly<Record<string, readonly string[]>> = {
  storefront: ['web', 'search-ui', 'account-ui', 'cart-ui', 'cms-edge', 'image-resize', 'recommend', 'sitemap',
    'wishlist-ui', 'compare-ui', 'promo-ui', 'help-ui'],
  checkout: ['cart', 'pricing', 'payment', 'tax', 'order', 'fraud-check', 'invoice', 'refund', 'shipping-quote',
    'voucher', 'gift-card', 'receipt'],
  catalog: ['catalog', 'search', 'inventory', 'reviews', 'media', 'import', 'feeds', 'taxonomy', 'availability',
    'bundles', 'price-feed', 'suggest'],
  platform: ['auth', 'gateway', 'events', 'mailer', 'scheduler', 'audit', 'config', 'flags', 'rate-limit',
    'session', 'webhooks', 'notifier'],
  fulfilment: ['warehouse', 'picking', 'packing', 'carrier', 'tracking', 'returns', 'labels', 'slots',
    'stock-sync', 'dispatch', 'courier-api', 'proof'],
};

function complex(): StructureData {
  const project = 'PROJ-SHOP';
  const nodes: StructureNode[] = [node('b', 'boundary', 'Shop platform', null)];
  const service = (id: string, label: string, parent: string, environmentId: string, role: string, replicas: number) =>
    node(id, 'container', label, parent, { project, unitId: label, environmentId, role, replicas });
  nodes.push(node('env-prod', 'environment', 'Production', 'b', { environmentId: 'production', project }));
  for (const [region, label] of [['eu', 'Region EU'], ['us', 'Region US']] as const) {
    const scope = { environmentId: 'production', project };
    nodes.push(node(region, 'region', label, 'env-prod', scope));
    nodes.push(node(`${region}-k8s`, 'cluster', `prod-${region}`, region, { ...scope, detail: 'Kubernetes' }));
    for (const [namespace, list] of Object.entries(complexServices)) {
      nodes.push(node(`${region}-${namespace}`, 'namespace', namespace, `${region}-k8s`, scope));
      list.forEach((name, i) => nodes.push(service(`${region}-${namespace}-${name}`, name, `${region}-${namespace}`,
        'production', namespace, namespace === 'storefront' && i < 2 ? 4 : 2)));
    }
    nodes.push(node(`${region}-db`, 'managed', 'PostgreSQL', region,
      { ...scope, detail: 'database', probeObservation: 'probed', probe: 'tls', status: 'up' }));
    nodes.push(node(`${region}-mq`, 'managed', 'Message queue', region,
      { ...scope, detail: 'queue', probeObservation: 'not-observed' }));
    nodes.push(node(`${region}-obj`, 'managed', 'Object storage', region,
      { ...scope, detail: 'object storage', probeObservation: 'probed', probe: 'http', status: 'up' }));
  }
  const staging = { environmentId: 'staging', project };
  nodes.push(node('env-stage', 'environment', 'Staging', 'b', staging));
  nodes.push(node('stage-k8s', 'cluster', 'stage', 'env-stage', { ...staging, detail: 'Kubernetes' }));
  for (const [namespace, list] of Object.entries(complexServices)) {
    nodes.push(node(`st-${namespace}`, 'namespace', namespace, 'stage-k8s', staging));
    list.forEach(name => nodes.push(service(`st-${namespace}-${name}`, name, `st-${namespace}`, 'staging', namespace, 1)));
  }
  nodes.push(node('stage-db', 'managed', 'PostgreSQL', 'env-stage',
    { ...staging, detail: 'database', probeObservation: 'not-observed' }));
  const development = { environmentId: 'development', project };
  nodes.push(node('env-dev', 'environment', 'Development', 'b', development));
  nodes.push(node('dev-vm', 'vm', 'Shared development VM', 'env-dev', { ...development, detail: 'SSH Docker host' }));
  Object.entries(complexServices).flatMap(([namespace, list]) => list.slice(0, 3).map(name => [namespace, name] as const))
    .forEach(([namespace, name]) => nodes.push(service(`dev-${name}`, name, 'dev-vm', 'development', namespace, 1)));
  nodes.push(node('cdn', 'external', 'CDN', null));
  nodes.push(node('pay', 'external', 'Payment provider', null));
  nodes.push(node('idp', 'external', 'Identity provider', null));
  nodes.push(node('images', 'external', 'Container registry', null));
  return {
    nodes,
    links: [
      link('cdn', 'eu-storefront-web', 'call', 'HTTPS'),
      link('cdn', 'us-storefront-web', 'call', 'HTTPS'),
      link('eu-checkout-payment', 'pay', 'call', 'payments'),
      link('us-checkout-payment', 'pay', 'call', 'payments'),
      link('eu-checkout-order', 'eu-db', 'call', 'orders'),
      link('us-checkout-order', 'us-db', 'call', 'orders'),
      link('eu-platform-auth', 'idp', 'call', 'sign-in'),
      link('eu-catalog-media', 'eu-obj', 'call', 'media'),
      link('images', 'stage-k8s', 'artifact', 'image pull'),
      link('images', 'dev-vm', 'artifact', 'image pull'),
      link('st-checkout-order', 'stage-db', 'call', 'orders'),
    ],
  };
}

export const complexFixture: StructureData = complex();
export const structureFixtures: Readonly<Record<'complex' | 'agt' | 'workspace', StructureData>> =
  { complex: complexFixture, agt: agtFixture, workspace: workspaceFixture };

// AGT-3954: an installation where nothing is declared except one spare machine and the
// externals of a placement file. Every other node comes from host reports and from what
// the API knows about itself, as GET /api/v1/operations/structure returns them.
const seen = '2026-11-17T12:00:00Z';
const observedNode = (id: string, kind: string, label: string, parentId: string | null,
  extra: Partial<StructureNode> = {}): StructureNode => node(id, kind, label, parentId, {
  provenance: 'observed', sourceRef: 'host report', observation: { difference: 'undeclared', health: 'up', observedAt: seen }, ...extra });
const reviewHost = (n: number): StructureNode[] => [
  observedNode(`machine:review-${n}`, 'container', `review-${n}`, 'machine:linux-vm', { detail: `review-host-${n}`,
    observation: { difference: 'undeclared', health: 'up', observedAt: seen, version: '2.0', expectedVersion: '2.0',
      cpuPercent: 37, memoryUsedBytes: 8 * 1024 ** 3, memoryTotalBytes: 32 * 1024 ** 3, activeGates: 0 },
    facts: {
      hostId: `review-${n}`, machineKind: 'container', virtualization: 'docker', containerName: `review-host-${n}`,
      roles: ['review'], lastSeenAt: seen,
      performance: { observedAt: seen, cpuPercent: 37, cpuCores: 24, load1: 2.5, load5: 2, load15: 1.5,
        memoryUsedBytes: 8 * 1024 ** 3, memoryTotalBytes: 32 * 1024 ** 3,
        disks: [{ mount: '/srv/work', purpose: 'runner work', usedBytes: 60 * 1024 ** 3, freeBytes: 40 * 1024 ** 3, totalBytes: 100 * 1024 ** 3 }],
        networkReceiveBytesPerSecond: 1200, networkTransmitBytesPerSecond: 3400 },
      capacity: { busySlots: 1, freeSlots: 1, totalSlots: 2, runningCards: n === 1 ? ['AGT-7'] : [] },
      release: { version: '2.0', stableVersion: '2.0', state: 'current', behind: false, pendingInstall: false },
      warnings: { tools: [{ name: 'claude', kind: 'cli', version: '1.2.0', targetVersion: '1.3.0', state: 'behind' },
        { name: 'dotnet', kind: 'toolchain', version: '10.0.100', state: 'ok' }],
        missingRequired: n === 1 ? ['toolchain:playwright'] : [], behindRequired: ['claude'], releaseBehind: false,
        pendingHostInstall: false, linksDown: [],
        blockedCards: n === 1 ? [{ key: 'AGT-10', state: 'refused', reason: 'toolchain:playwright' }] : [] },
    } }),
  observedNode(`component:review-${n}:review-${n}-runner`, 'process', 'review daemon', `machine:review-${n}`,
    { unitId: `review-${n}-runner`, role: 'review' }),
];
const control = (from: string, latencyMs: number): StructureLink => ({
  id: `link:${from}>component:studio-server:api:control`, from, to: 'component:studio-server:api', kind: 'control',
  label: 'Task Server connection', sourceRef: 'host report',
  observation: { difference: 'undeclared', health: 'up', observedAt: seen }, facts: { state: 'up', latencyMs, probedAt: seen, transport: 'https' } });

export const observedStructure: StructureData = {
  revision: 40, generatedAt: seen, schemaVersion: 2,
  nodes: [
    node('installation', 'boundary', 'Installation', null, { sourceRef: 'Connectivity:ManifestPath',
      observation: { difference: 'declared-only', health: 'not observed' } }),
    node('machine:spare', 'physical', 'Spare machine', 'installation', { sourceRef: 'Structure:PlacementPath#machines/spare',
      observation: { difference: 'declared-only', health: 'not observed' } }),
    node('external:cdn', 'external', 'CDN proxy', null, { sourceRef: 'Structure:PlacementPath#externals/cdn',
      observation: { difference: 'unreachable-by-design', health: 'not observed' } }),
    node('external:source-host', 'external', 'Source host', null, { sourceRef: 'Structure:PlacementPath#externals/source-host',
      observation: { difference: 'unreachable-by-design', health: 'not observed' } }),
    node('external:registry', 'external', 'Container registry', null, { sourceRef: 'Structure:PlacementPath#externals/registry',
      observation: { difference: 'unreachable-by-design', health: 'not observed' } }),
    node('external:models', 'external', 'Model providers', null, { sourceRef: 'Structure:PlacementPath#externals/models',
      observation: { difference: 'unreachable-by-design', health: 'not observed' } }),
    observedNode('machine:studio-server', 'server', 'Studio server', 'installation', { sourceRef: 'studio self observation',
      observation: { difference: 'undeclared', health: 'up', observedAt: seen, detail: 'The API reports from this machine' },
      facts: { machineKind: 'vm', virtualization: 'kvm', lastSeenAt: seen,
        performance: { observedAt: seen, cpuPercent: 12, cpuCores: 12, load1: 1.2, load5: 1.1, load15: 0.9,
          memoryUsedBytes: 20 * 1024 ** 3, memoryTotalBytes: 62 * 1024 ** 3,
          disks: [{ mount: '/', purpose: 'instance data', usedBytes: 84 * 1024 ** 3, freeBytes: 16 * 1024 ** 3, totalBytes: 100 * 1024 ** 3 }],
          networkReceiveBytesPerSecond: 20480, networkTransmitBytesPerSecond: 51200 },
        dataLocations: [
          { id: 'data:studio-server:workspace-repository', role: 'workspace-repository', label: 'Workspace repository', sourceRef: 'TaskRepository',
            exists: true, sizeBytes: 3 * 1024 ** 3, sizeComplete: true, measuredAt: seen, backupCovered: true, gitRemoteCovered: true, coverage: 'both' },
          { id: 'data:studio-server:project-checkouts', role: 'project-checkouts', label: 'Project checkouts', sourceRef: 'project registry',
            exists: true, sizeBytes: 9 * 1024 ** 3, sizeComplete: false, measuredAt: seen, backupCovered: false, gitRemoteCovered: true, coverage: 'git-remote' },
          { id: 'data:studio-server:application-state', role: 'application-state', label: 'Application state directory', sourceRef: 'local state root',
            exists: true, sizeBytes: 200 * 1024 ** 2, sizeComplete: true, measuredAt: seen, backupCovered: true, gitRemoteCovered: false, coverage: 'backup' },
          { id: 'data:studio-server:secrets-key', role: 'secrets-key', label: 'Secrets key', sourceRef: 'Secrets:KeyFile',
            exists: true, sizeBytes: 64, sizeComplete: true, measuredAt: seen, backupCovered: false, gitRemoteCovered: false, coverage: 'none' },
          { id: 'data:studio-server:edge-certificates', role: 'edge-certificates', label: 'Edge certificates', sourceRef: 'Structure:EdgeCertificatesPath',
            exists: false, sizeBytes: null, sizeComplete: false, measuredAt: null, backupCovered: false, gitRemoteCovered: false, coverage: 'none' },
        ] } }),
    observedNode('component:studio-server:api', 'container', 'API', 'machine:studio-server', { unitId: 'api', role: 'api',
      sourceRef: 'studio self observation', detail: 'stack-api-1',
      observation: { difference: 'undeclared', health: 'up', observedAt: seen, version: '2.0', expectedVersion: '2.0' } }),
    observedNode('component:studio-server:web', 'container', 'Web frontend', 'machine:studio-server', { unitId: 'web', role: 'web',
      sourceRef: 'studio self observation', detail: 'stack-frontend-1' }),
    observedNode('component:studio-server:edge', 'container', 'Edge proxy', 'machine:studio-server', { unitId: 'edge', role: 'edge',
      sourceRef: 'studio self observation', detail: 'stack-edge-1' }),
    observedNode('component:studio-server:engine', 'container', 'Orchestrator engine', 'machine:studio-server', { unitId: 'engine', role: 'engine',
      sourceRef: 'studio self observation', detail: 'stack-orchestrator-engine-1' }),
    observedNode('observed-container:runner-host:deploy-stage-api-1', 'container', 'deploy-stage-api-1', 'machine:studio-server', {
      sourceRef: 'host capability report', observation: { difference: 'undeclared', health: 'down', observedAt: seen, detail: 'exited' } }),
    observedNode('machine:runner-host', 'host', 'runner-host', 'machine:studio-server', {
      facts: { hostId: 'runner-host', machineKind: 'vm', virtualization: 'kvm', roles: ['coding', 'review'], lastSeenAt: seen,
        capacity: { busySlots: 2, freeSlots: 2, totalSlots: 4, runningCards: ['AGT-3', 'AGT-5'] },
        release: { version: '1.9', stableVersion: '2.0', state: 'behind', behind: true, pendingInstall: true } },
      observation: { difference: 'differs', health: 'up', observedAt: seen, version: '1.9', expectedVersion: '2.0', behind: true } }),
    observedNode('component:runner-host:coding-runner', 'process', 'coding daemon', 'machine:runner-host', { unitId: 'coding-runner', role: 'coding' }),
    observedNode('component:runner-host:review-runner', 'process', 'review daemon', 'machine:runner-host', { unitId: 'review-runner', role: 'review' }),
    observedNode('machine:home-lab', 'physical', 'home-lab', 'installation', {
      observation: { difference: 'undeclared', health: 'not observed', detail: 'Named by a reporting host' } }),
    observedNode('machine:linux-vm', 'vm', 'linux-vm', 'machine:home-lab', {
      observation: { difference: 'undeclared', health: 'not observed', detail: 'Named by a reporting host' } }),
    ...[1, 2, 3].flatMap(reviewHost),
    observedNode('machine:gate-1', 'container', 'gate-1', 'machine:linux-vm', { detail: 'gate-host',
      facts: { hostId: 'gate-1', machineKind: 'container', roles: ['gate'], lastSeenAt: seen,
        capacity: { busySlots: 0, freeSlots: 2, totalSlots: 2, runningCards: [] } } }),
    observedNode('component:gate-1:gate-runner', 'process', 'gate daemon', 'machine:gate-1', { unitId: 'gate-runner', role: 'gate' }),
    // An older host reports no placement: it still shows, flat, and has stopped reporting.
    observedNode('machine:old-host', 'host', 'old-host', 'installation', {
      observation: { difference: 'stale', health: 'stale', observedAt: '2026-11-17T11:40:00Z' },
      facts: { hostId: 'old-host', roles: ['coding'], lastSeenAt: '2026-11-17T11:40:00Z' } }),
  ],
  links: [
    { id: 'link:component:studio-server:edge>external:cdn:call', from: 'component:studio-server:edge', to: 'external:cdn', kind: 'call',
      label: 'Public traffic', sourceRef: 'Structure:PlacementPath#structureLinks/0', observation: { difference: 'declared-only', health: 'not observed' } },
    control('machine:runner-host', 0.8),
    control('machine:review-1', 4.5),
    control('machine:gate-1', 3.9),
  ],
};
