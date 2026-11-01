// Scenario data for the AGT-3349 Dossier figures and prototype.
// Roles only: no machine names, addresses or account names.
// Status and version values are illustrative, not observations.
(function (root) {
  'use strict';
  const S = {};

  // parent: placement parent (host view). group: logical parent (container view).
  // prov: declared (declared, not observed) | undeclared (observed, not declared); default both.
  // rep: replica key; identical siblings collapse into one node with a count below level 3.

  S.simple = {
    title: 'Simple setup: one website in one container on one VM',
    desc: 'A system boundary contains one cloud VM with one website container and its edge process. The built-in registry and the visitors are outside the boundary.',
    nodes: [
      { id: 'out', kind: 'outside', label: 'Outside the boundary', dir: 'col' },
      { id: 'web', kind: 'external', label: 'Visitors', sub: 'public DNS name', parent: 'out' },
      { id: 'reg', kind: 'external', label: 'Built-in registry', sub: 'image source', parent: 'out', status: 'ok' },
      { id: 'b', kind: 'boundary', label: 'Website · production' },
      { id: 'vm', kind: 'vm', label: 'Web VM', kindSub: 'cloud · Linux', parent: 'b', status: 'ok', dir: 'col' },
      { id: 'edge', kind: 'process', label: 'Edge proxy', sub: 'HTTPS :443', parent: 'vm', group: 'u', status: 'ok' },
      { id: 'site', kind: 'container', label: 'website', sub: 'container :8080', parent: 'vm', group: 'u', ver: 'rel 1.4.2', status: 'ok' },
      { id: 'u', kind: 'group', label: 'Website', kindSub: 'one unit', parent: 'b', only: 'container' },
    ],
    links: [
      { from: 'web', to: 'edge', kind: 'call', label: 'HTTPS' },
      { from: 'edge', to: 'site', kind: 'call' },
      { from: 'reg', to: 'site', kind: 'artifact', label: 'image pull' },
    ],
  };

  S.agt = {
    title: 'AGT today',
    desc: 'The AGT installation: a Studio machine without containers, a Linux runner host with two systemd services and two containers, and a home-lab physical machine with a VM that runs three review-host containers and one gate container. GitHub, the container registry and model providers are outside the boundary.',
    rootDir: 'col',
    nodes: [
      { id: 'b', kind: 'boundary', label: 'AGT installation', wrap: 1420 },
      { id: 'loc-cloud', kind: 'location', label: 'Cloud provider', parent: 'b', wrap: 420 },
      { id: 'loc-ws', kind: 'location', label: 'Operator site', parent: 'b', wrap: 360 },
      { id: 'loc-lab', kind: 'location', label: 'Home lab', parent: 'b', wrap: 520 },

      { id: 'studio', kind: 'host', label: 'Studio machine', kindSub: 'Windows · no containers', parent: 'loc-ws', dir: 'col', status: 'ok' },
      { id: 'api', kind: 'process', label: 'Studio backend', sub: ':5031 hosts · :5032 operator', parent: 'studio', group: 'g-control', ver: 'rel 10.31.2', status: 'ok' },
      { id: 'proxy', kind: 'process', label: 'Frontend proxy', sub: ':4011', parent: 'studio', group: 'g-control', ver: 'rel 10.31.2', status: 'ok' },
      { id: 'upd', kind: 'process', label: 'Update service', sub: ':5039', parent: 'studio', group: 'g-ops', ver: 'rel 10.31.2', status: 'ok' },
      { id: 'wd', kind: 'process', label: 'Watchdog', parent: 'studio', group: 'g-ops', status: 'ok' },
      { id: 'repo', kind: 'store', label: 'Workspace repository', parent: 'studio', group: 'g-data', status: 'ok' },
      { id: 'sec', kind: 'store', label: 'Secret store', parent: 'studio', group: 'g-data', status: 'unknown' },

      { id: 'runner', kind: 'server', label: 'Linux runner host', kindSub: 'Linux · Docker', parent: 'loc-cloud', wrap: 380, status: 'ok' },
      { id: 'coding', kind: 'process', label: 'Coding host', sub: 'systemd · agent-host', parent: 'runner', group: 'g-exec', ver: 'host 10.31.2', status: 'ok' },
      { id: 'review', kind: 'process', label: 'Review host', sub: 'systemd · agent-host', parent: 'runner', group: 'g-exec', ver: 'host 10.31.2', status: 'ok' },
      { id: 'engine', kind: 'container', label: 'Orchestrator Engine', sub: 'Docker', parent: 'runner', group: 'g-control', ver: 'img 10.30.1', behind: true, status: 'ok' },
      { id: 'studio2', kind: 'container', label: 'Studio, second instance', sub: 'Docker', parent: 'runner', group: 'g-control', ver: 'img 10.29.3', behind: true, drift: 'not declared', prov: 'undeclared', status: 'ok' },
      { id: 'promo', kind: 'store', label: 'Promotion checkout', parent: 'runner', group: 'g-verify', status: 'ok' },
      { id: 'verify', kind: 'store', label: 'Verification checkout', parent: 'runner', group: 'g-verify', status: 'ok' },

      { id: 'lab', kind: 'host', label: 'Home-lab machine', kindSub: 'physical', parent: 'loc-lab', status: 'ok' },
      { id: 'labvm', kind: 'vm', label: 'Home-lab VM', kindSub: 'Linux · Docker', parent: 'lab', wrap: 460, status: 'warn' },
      { id: 'rv1', kind: 'container', label: 'Review host 1', repLabel: 'Review host', sub: 'Docker', rep: 'rv', parent: 'labvm', group: 'g-exec', ver: 'local 10.28', behind: true, status: 'ok' },
      { id: 'rv2', kind: 'container', label: 'Review host 2', repLabel: 'Review host', sub: 'Docker', rep: 'rv', parent: 'labvm', group: 'g-exec', ver: 'local 10.28', behind: true, status: 'ok' },
      { id: 'rv3', kind: 'container', label: 'Review host 3', repLabel: 'Review host', sub: 'Docker', rep: 'rv', parent: 'labvm', group: 'g-exec', ver: 'local 10.28', behind: true, status: 'warn' },
      { id: 'gate', kind: 'container', label: 'Build and test gate', sub: 'Docker · 3 slots', parent: 'labvm', group: 'g-verify', ver: 'rel 10.31.2', status: 'ok' },

      { id: 'g-control', kind: 'group', label: 'Control plane', parent: 'b', only: 'container', dir: 'col' },
      { id: 'g-exec', kind: 'group', label: 'Execution', parent: 'b', only: 'container', dir: 'col' },
      { id: 'g-verify', kind: 'group', label: 'Verification and release', parent: 'b', only: 'container', dir: 'col' },
      { id: 'g-ops', kind: 'group', label: 'Lifecycle', parent: 'b', only: 'container', dir: 'col' },
      { id: 'g-data', kind: 'group', label: 'Data', parent: 'b', only: 'container', dir: 'col' },

      { id: 'out', kind: 'outside', label: 'Outside the boundary', wrap: 1200 },
      { id: 'gh', kind: 'external', label: 'GitHub', sub: 'origin · App · Actions', parent: 'out' },
      { id: 'cr', kind: 'external', label: 'Container registry', sub: 'role and engine images', parent: 'out' },
      { id: 'mp', kind: 'external', label: 'Model providers', sub: 'CLI sign-in', parent: 'out' },
    ],
    links: [
      { from: 'api', to: 'coding', kind: 'tunnel', label: 'reverse tunnel :15031' },
      { from: 'api', to: 'rv1', kind: 'tunnel', label: 'reverse tunnel' },
      { from: 'api', to: 'gate', kind: 'ssh', label: 'SSH gate transport', offset: 18 },
      { from: 'promo', to: 'gate', kind: 'artifact', label: 'release package', drift: true },
      { from: 'gate', to: 'upd', kind: 'artifact', label: 'fetch package', offset: -18 },
      { from: 'coding', to: 'gh', kind: 'call', label: 'Git' },
      { from: 'cr', to: 'engine', kind: 'artifact', label: 'image pull' },
      { from: 'review', to: 'mp', kind: 'call', label: 'model calls' },
    ],
  };

  // Complex: three environments, two production regions, Kubernetes, managed data.
  const services = {
    storefront: ['web', 'search-ui', 'account-ui', 'cart-ui', 'cms-edge', 'image-resize', 'recommend', 'sitemap'],
    checkout: ['cart', 'pricing', 'payment', 'tax', 'order', 'fraud-check'],
    catalog: ['catalog', 'search', 'inventory', 'reviews', 'media', 'import', 'feeds'],
    platform: ['auth', 'gateway', 'events', 'mailer', 'scheduler'],
  };
  const cx = { title: 'Complex setup', desc: '', rootWrap: 1700, nodes: [], links: [] };
  cx.nodes.push({ id: 'out', kind: 'outside', label: 'Outside the boundary', dir: 'col' });
  cx.nodes.push({ id: 'b', kind: 'boundary', label: 'Shop platform', wrap: 1240 });
  const regions = [['eu', 'Region EU'], ['us', 'Region US']];
  cx.nodes.push({ id: 'env-prod', kind: 'environment', label: 'Production', parent: 'b', wrap: 1220 });
  for (const [r, label] of regions) {
    cx.nodes.push({ id: `${r}`, kind: 'location', label, parent: 'env-prod', wrap: 580 });
    cx.nodes.push({ id: `${r}-k8s`, kind: 'cluster', label: `prod-${r}`, kindSub: 'managed control plane', parent: r, ver: 'k8s 1.33', status: r === 'us' ? 'warn' : 'ok' });
    for (const [ns, list] of Object.entries(services)) {
      cx.nodes.push({ id: `${r}-${ns}`, kind: 'namespace', label: ns, parent: `${r}-k8s`, group: `g-${ns}`, wrap: 470 });
      list.forEach((svc, i) => cx.nodes.push({ id: `${r}-${ns}-${svc}`, kind: 'container', label: svc, parent: `${r}-${ns}`, group: `g-${ns}`, rep: `${ns}-${svc}`,
        replicas: ns === 'storefront' && i < 2 ? 4 : 2, sub: 'deployment', ver: svc === 'pricing' && r === 'us' ? 'v3.8.0' : undefined,
        status: r === 'us' && svc === 'payment' ? 'warn' : r === 'us' && svc === 'import' ? 'down' : 'ok', drift: svc === 'pricing' && r === 'us' ? 'image differs' : undefined,
        project: 'shop' }));
    }
    cx.nodes.push({ id: `${r}-db`, kind: 'managed', label: 'PostgreSQL', sub: 'managed · probed via TLS', parent: r, group: 'g-data', rep: 'pg', status: 'ok', prov: 'declared' });
    cx.nodes.push({ id: `${r}-mq`, kind: 'managed', label: 'Message queue', sub: 'managed · not observed', parent: r, group: 'g-data', rep: 'mq', prov: 'declared', status: 'unknown' });
    cx.nodes.push({ id: `${r}-obj`, kind: 'managed', label: 'Object storage', sub: 'managed · probed via HTTPS', parent: r, group: 'g-data', rep: 'obj', prov: 'declared', status: 'ok' });
  }
  cx.nodes.push({ id: 'env-stage', kind: 'environment', label: 'Staging', parent: 'b', wrap: 900 });
  cx.nodes.push({ id: 'stage-k8s', kind: 'cluster', label: 'stage', kindSub: 'one region', parent: 'env-stage', status: 'ok' });
  for (const [ns, list] of Object.entries(services)) {
    cx.nodes.push({ id: `st-${ns}`, kind: 'namespace', label: ns, parent: 'stage-k8s', only: 'host' });
    list.forEach(svc => cx.nodes.push({ id: `st-${ns}-${svc}`, kind: 'container', label: svc, parent: `st-${ns}`, only: 'host', status: 'ok', sub: 'deployment' }));
  }
  cx.nodes.push({ id: 'stage-db', kind: 'managed', label: 'PostgreSQL', sub: 'managed', parent: 'env-stage', only: 'host', prov: 'declared', status: 'ok' });
  cx.nodes.push({ id: 'env-dev', kind: 'environment', label: 'Development', parent: 'b', wrap: 500 });
  cx.nodes.push({ id: 'dev-vm', kind: 'vm', label: 'Shared dev VM', kindSub: 'Docker Compose', parent: 'env-dev', only: 'host', status: 'ok' });
  Object.values(services).flat().slice(0, 12).forEach(svc => cx.nodes.push({ id: `dev-${svc}`, kind: 'container', label: svc, parent: 'dev-vm', only: 'host', status: 'ok' }));
  for (const ns of Object.keys(services)) cx.nodes.push({ id: `g-${ns}`, kind: 'group', label: ns, parent: 'b', only: 'container' });
  cx.nodes.push({ id: 'g-data', kind: 'group', label: 'Managed data', parent: 'b', only: 'container' });
  cx.nodes.push({ id: 'cdn', kind: 'external', label: 'CDN', sub: 'edge cache', parent: 'out' });
  cx.nodes.push({ id: 'pay', kind: 'external', label: 'Payment provider', sub: 'API', parent: 'out' });
  cx.nodes.push({ id: 'idp', kind: 'external', label: 'Identity provider', sub: 'OIDC', parent: 'out' });
  cx.links.push({ from: 'cdn', to: 'eu-storefront-web', kind: 'call', label: 'HTTPS' });
  cx.links.push({ from: 'cdn', to: 'us-storefront-web', kind: 'call', label: 'HTTPS' });
  cx.links.push({ from: 'eu-checkout-payment', to: 'pay', kind: 'call', label: 'payments' });
  cx.links.push({ from: 'eu-checkout-order', to: 'eu-db', kind: 'call' });
  cx.links.push({ from: 'us-checkout-order', to: 'us-db', kind: 'call' });
  S.complex = cx;
  cx.desc = 'A shop platform with production in two regions on managed Kubernetes clusters, namespaces with 26 services per region, managed PostgreSQL, queue and object storage declared but not observed directly, plus a staging cluster and a shared development VM. CDN, payment and identity providers are outside.';

  S.library = {
    title: 'Library project: packages and registries',
    desc: 'A library project without hosts publishes an npm package to an internal JFrog registry and to npm, and a demo image to Docker Hub. Workspace projects consume different versions.',
    rootWrap: 1500,
    nodes: [
      { id: 'p', kind: 'project', label: 'Chat UI library', kindSub: 'no hosts', dir: 'col', wrap: 260 },
      { id: 'a-npm', kind: 'artifact', label: 'npm package', sub: 'built by the release gate', parent: 'p', ver: '0.5.0-rc.2' },
      { id: 'a-img', kind: 'artifact', label: 'Demo image', sub: 'static demo pages', parent: 'p', ver: '0.4.1' },
      { id: 'regs', kind: 'outside', label: 'Registries', dir: 'col', wrap: 360 },
      { id: 'jf', kind: 'registry', label: 'JFrog', kindSub: 'internal · npm-internal', parent: 'regs', status: 'ok', dir: 'col' },
      { id: 'jf-next', kind: 'package', label: 'next', sub: 'published 2 days ago', parent: 'jf', ver: '0.5.0-rc.2', status: 'ok' },
      { id: 'jf-latest', kind: 'package', label: 'latest', sub: 'published 19 days ago', parent: 'jf', ver: '0.4.1', status: 'ok' },
      { id: 'npm', kind: 'registry', label: 'npm', kindSub: 'external · public', parent: 'regs', status: 'ok', dir: 'col' },
      { id: 'npm-latest', kind: 'package', label: 'latest', sub: 'published 19 days ago', parent: 'npm', ver: '0.4.1', status: 'ok' },
      { id: 'npm-next', kind: 'package', label: 'next', sub: 'not published yet', parent: 'npm', ver: '0.5.0-rc.1', drift: 'behind JFrog', behind: true, status: 'ok' },
      { id: 'dh', kind: 'registry', label: 'Docker Hub', kindSub: 'external · public', parent: 'regs', status: 'warn', dir: 'col' },
      { id: 'dh-latest', kind: 'package', label: 'latest', sub: 'lookup failed 3 h ago', parent: 'dh', ver: '0.4.1', status: 'warn' },
      { id: 'cons', kind: 'outside', label: 'Consumers in this workspace', dir: 'col', wrap: 300 },
      { id: 'c-agt', kind: 'external', label: 'Agent Studio frontend', sub: 'pins 0.4.1 from npm', parent: 'cons', ver: '0.4.1' },
      { id: 'c-web', kind: 'external', label: 'Website', sub: 'pins 0.3.2 from npm', parent: 'cons', ver: '0.3.2', behind: true, drift: '2 releases behind' },
      { id: 'c-tool', kind: 'external', label: 'Internal tool', sub: 'next from JFrog', parent: 'cons', ver: '0.5.0-rc.2' },
    ],
    links: [
      { from: 'a-npm', to: 'jf-next', kind: 'publish', label: 'publish' },
      { from: 'a-npm', to: 'npm-latest', kind: 'publish', label: 'publish (tag)' },
      { from: 'a-img', to: 'dh-latest', kind: 'publish', label: 'push' },
      { from: 'npm-latest', to: 'c-agt', kind: 'consume' },
      { from: 'npm-latest', to: 'c-web', kind: 'consume', label: 'older version' },
      { from: 'jf-next', to: 'c-tool', kind: 'consume' },
    ],
  };

  // Workspace: AGT, Website and the library in one picture. project marks ownership.
  S.workspace = {
    title: 'Workspace whole picture',
    desc: 'Three projects in one workspace. AGT runs on its three machines; the Website project runs on its own VM, where an AGT run-target agent deploys it; the library has only registries.',
    rootWrap: 1600,
    rootDir: 'col',
    nodes: [
      { id: 'ws-row', kind: 'scope', label: '', project: '*', wrap: 1060 },
      { id: 'b-agt', kind: 'boundary', label: 'AGT installation', parent: 'ws-row', project: 'agt', wrap: 520 },
      { id: 'w-studio', kind: 'host', label: 'Studio machine', kindSub: 'Windows', parent: 'b-agt', project: 'agt', status: 'ok' },
      { id: 'w-api', kind: 'process', label: 'Studio backend', parent: 'w-studio', project: 'agt', status: 'ok', ver: 'rel 10.31.2' },
      { id: 'w-upd', kind: 'process', label: 'Update service', parent: 'w-studio', project: 'agt', status: 'ok' },
      { id: 'w-runner', kind: 'server', label: 'Linux runner host', parent: 'b-agt', project: 'agt', status: 'ok' },
      { id: 'w-coding', kind: 'process', label: 'Coding host', parent: 'w-runner', project: 'agt', status: 'ok' },
      { id: 'w-review', kind: 'process', label: 'Review host', parent: 'w-runner', project: 'agt', status: 'ok' },
      { id: 'w-engine', kind: 'container', label: 'Orchestrator Engine', parent: 'w-runner', project: 'agt', status: 'ok' },
      { id: 'w-lab', kind: 'host', label: 'Home-lab machine', parent: 'b-agt', project: 'agt', status: 'warn' },
      { id: 'w-labvm', kind: 'vm', label: 'Home-lab VM', parent: 'w-lab', project: 'agt', status: 'warn' },
      { id: 'w-rv', kind: 'container', label: 'Review host', parent: 'w-labvm', project: 'agt', replicas: 3, status: 'warn' },
      { id: 'w-gate', kind: 'container', label: 'Gate', parent: 'w-labvm', project: 'agt', status: 'ok' },

      { id: 'b-web', kind: 'boundary', label: 'Website', parent: 'ws-row', project: 'web', wrap: 460 },
      { id: 'w-env', kind: 'environment', label: 'Production', parent: 'b-web', project: 'web', wrap: 430 },
      { id: 'w-webvm', kind: 'vm', label: 'Web VM', kindSub: 'cloud · Linux', parent: 'w-env', project: 'web', status: 'ok' },
      { id: 'w-caddy', kind: 'process', label: 'Edge proxy', parent: 'w-webvm', project: 'web', status: 'ok' },
      { id: 'w-site', kind: 'container', label: 'website', parent: 'w-webvm', project: 'web', ver: 'rel 1.4.2', status: 'ok' },
      { id: 'w-docs', kind: 'container', label: 'docs site', parent: 'w-webvm', project: 'web', ver: 'rel 0.9.0', status: 'ok' },
      { id: 'w-rt', kind: 'process', label: 'Run-target agent', sub: 'deploys the containers', parent: 'w-webvm', project: 'agt', status: 'ok' },

      { id: 'b-lib', kind: 'project', label: 'Chat UI library', kindSub: 'no hosts', parent: 'ws-row', project: 'lib', dir: 'col' },
      { id: 'w-pkg', kind: 'artifact', label: 'npm package', parent: 'b-lib', project: 'lib', ver: '0.4.1' },

      { id: 'out', kind: 'outside', label: 'Shared, outside every boundary', wrap: 760, project: '*' },
      { id: 'w-gh', kind: 'external', label: 'GitHub', parent: 'out', project: '*' },
      { id: 'w-npm', kind: 'registry', label: 'npm', kindSub: 'external', parent: 'out', project: 'lib', dir: 'col' },
      { id: 'w-npm-l', kind: 'package', label: 'latest', parent: 'w-npm', project: 'lib', ver: '0.4.1', status: 'ok' },
      { id: 'w-reg', kind: 'external', label: 'Built-in registry', sub: 'website images', parent: 'out', project: 'web' },
    ],
    links: [
      { from: 'w-api', to: 'w-coding', kind: 'tunnel' },
      { from: 'w-api', to: 'w-rt', kind: 'call', label: 'desired state' },
      { from: 'w-reg', to: 'w-site', kind: 'artifact', label: 'image pull' },
      { from: 'w-pkg', to: 'w-npm-l', kind: 'publish', label: 'publish' },
      { from: 'w-npm-l', to: 'w-site', kind: 'consume', label: 'build dependency' },
    ],
  };


  S.key = {
    title: 'Visual language key',
    desc: 'One example of every shape: system boundary, location, physical or cloud host, VM, process, container, data store, external service, managed service, registry and package, plus status glyphs, provenance outlines, replica counts and link styles.',
    rootWrap: 1300,
    nodes: [
      { id: 'kb', kind: 'boundary', label: 'System boundary', wrap: 760 },
      { id: 'kl', kind: 'location', label: 'Location', kindLabel: 'Site, region or environment', parent: 'kb', wrap: 720 },
      { id: 'kh', kind: 'host', label: 'Host', kindSub: 'physical or cloud', parent: 'kl', dir: 'col' },
      { id: 'kp', kind: 'process', label: 'Process', sub: 'systemd or service · :port', parent: 'kh', status: 'ok' },
      { id: 'kc', kind: 'container', label: 'Container', sub: 'Docker', parent: 'kh', ver: 'rel 1.4.2', status: 'ok' },
      { id: 'ks', kind: 'store', label: 'Data store', parent: 'kh', status: 'ok' },
      { id: 'kv', kind: 'vm', label: 'VM', kindSub: 'nests in a host', parent: 'kl', dir: 'col' },
      { id: 'kr', kind: 'container', label: 'Container', sub: 'identical replicas', parent: 'kv', replicas: 3, status: 'ok' },
      { id: 'kst', kind: 'group', label: 'Status and provenance', kindLabel: 'Glyph shape and word, never colour alone', parent: 'kb', wrap: 340 },
      { id: 's1', kind: 'container', label: 'Healthy', sub: 'small calm glyph', parent: 'kst', status: 'ok' },
      { id: 's2', kind: 'container', label: 'Degraded', sub: 'triangle', parent: 'kst', status: 'warn' },
      { id: 's3', kind: 'container', label: 'Down', sub: 'square', parent: 'kst', status: 'down' },
      { id: 's4', kind: 'container', label: 'Not observed', sub: 'hollow, no source', parent: 'kst', status: 'unknown' },
      { id: 's5', kind: 'container', label: 'Declared only', sub: 'dashed outline', parent: 'kst', prov: 'declared', status: 'unknown' },
      { id: 's6', kind: 'container', label: 'Undeclared', sub: 'hatched, observed only', parent: 'kst', prov: 'undeclared', status: 'ok' },
      { id: 's7', kind: 'container', label: 'Differs', ver: 'img 1.4.0', parent: 'kst', behind: true, drift: '≠ declared', status: 'ok' },
      { id: 'ko', kind: 'outside', label: 'Outside the boundary', dir: 'col' },
      { id: 'ke', kind: 'external', label: 'External service', sub: 'not observed by the platform', parent: 'ko' },
      { id: 'km', kind: 'managed', label: 'Managed service', sub: 'declared · probed via TLS', parent: 'ko', prov: 'declared', status: 'ok' },
      { id: 'kreg', kind: 'registry', label: 'Registry', kindSub: 'internal or external', parent: 'ko', dir: 'col' },
      { id: 'kpkg', kind: 'package', label: 'Channel', sub: 'published 2 days ago', parent: 'kreg', ver: '1.4.2', status: 'ok' },
    ],
    links: [
      { from: 'ke', to: 'kc', kind: 'call', label: 'Call or request' },
      { from: 'kp', to: 'kr', kind: 'tunnel', label: 'Tunnel' },
      { from: 'kc', to: 'kr', kind: 'ssh', label: 'SSH transport' },
      { from: 'kpkg', to: 'kc', kind: 'artifact', label: 'Artifact or package flow' },
      { from: 'ks', to: 'km', kind: 'consume', label: 'Consumes or depends on' },
    ],
  };

  if (typeof module !== 'undefined' && module.exports) module.exports = S;
  root.DSV_SCENARIOS = S;
})(typeof globalThis !== 'undefined' ? globalThis : this);
