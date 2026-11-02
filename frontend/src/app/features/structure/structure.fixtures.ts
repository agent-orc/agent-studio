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
      { ...scope, detail: 'database', observation: 'probed', probe: 'tls', status: 'up' }));
    nodes.push(node(`${region}-mq`, 'managed', 'Message queue', region,
      { ...scope, detail: 'queue', observation: 'not-observed' }));
    nodes.push(node(`${region}-obj`, 'managed', 'Object storage', region,
      { ...scope, detail: 'object storage', observation: 'probed', probe: 'http', status: 'up' }));
  }
  const staging = { environmentId: 'staging', project };
  nodes.push(node('env-stage', 'environment', 'Staging', 'b', staging));
  nodes.push(node('stage-k8s', 'cluster', 'stage', 'env-stage', { ...staging, detail: 'Kubernetes' }));
  for (const [namespace, list] of Object.entries(complexServices)) {
    nodes.push(node(`st-${namespace}`, 'namespace', namespace, 'stage-k8s', staging));
    list.forEach(name => nodes.push(service(`st-${namespace}-${name}`, name, `st-${namespace}`, 'staging', namespace, 1)));
  }
  nodes.push(node('stage-db', 'managed', 'PostgreSQL', 'env-stage',
    { ...staging, detail: 'database', observation: 'not-observed' }));
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
