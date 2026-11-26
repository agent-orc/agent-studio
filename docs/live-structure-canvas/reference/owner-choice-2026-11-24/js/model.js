/* Structure model: nodes, links, volumes, and the two world layouts (wide, tall).
   Everything here is sample data for the prototype. No real host names or addresses. */
(function () {
  'use strict';
  const SV = (window.SV = window.SV || {});

  const nodes = {};
  const order = [];
  function node(id, kind, name, sub, parent, extra) {
    const n = Object.assign({ id, kind, name, sub, parent: parent || null, children: [], rect: { x: 0, y: 0, w: 0, h: 0 } }, extra || {});
    nodes[id] = n;
    order.push(n);
    if (parent) nodes[parent].children.push(n);
    return n;
  }

  node('install', 'boundary', 'Agent Studio installation', 'self-hosted', null, {
    facts: [['Scope', 'Everything the operator runs and is responsible for'], ['Machines', '3 (one rented, two on site)'], ['Outside', 'Public traffic, CDN proxy, any backup target']],
  });

  node('app', 'machine', 'App host', 'rented server · 12 cores · RAID 1', 'install', {
    headH: 70, cores: 12, memMax: 64, perfHost: true,
    facts: [['Role', 'Runs the Studio itself and the local runners'], ['Hardware', 'Rented server, 12 cores, 64 GB memory'], ['Disks', '2 disks in RAID 1, 480 GB usable'], ['Reachable', 'Public, through the CDN proxy only']],
  });
  node('stack', 'stack', 'container stack', 'compose project · 2 containers', 'app', {
    headH: 56,
    facts: [['Type', 'Container stack on the host runtime'], ['Members', 'Studio API, web app'], ['Network', 'Private stack network, one published port']],
  });
  node('web', 'container', 'web app', 'front end + origin for the CDN', 'stack', {
    unit: true, rate: 'origin',
    netNote: 'origin for the CDN proxy · :443', netShort: 'origin :443',
    facts: [['Image', 'studio-web'], ['Publishes', ':443 to the CDN proxy'], ['Talks to', 'Studio API on the stack network'], ['Restart policy', 'always']],
  });
  node('api', 'container', 'Studio API', 'task server · queues · backup feature', 'stack', {
    unit: true, rate: 'api', queues: true,
    volumes: ['proj', 'repo'],
    netNote: 'tunnel endpoint for the gate', netShort: 'tunnel endpoint',
    backupNote: 'backup feature: not configured',
    facts: [['Image', 'studio-api'], ['Owns', 'Task queues, project data, workspace repository'], ['Backup feature', 'Present, not configured'], ['Restart policy', 'always']],
  });
  node('coding', 'service', 'coding runner', 'host service · 10 slots', 'app', {
    unit: true, slots: 10, queue: 'coding',
    volumes: ['repo'],
    netNote: 'loopback to the Studio API', netShort: 'loopback',
    facts: [['Type', 'Host service (not containerised)'], ['Capacity', '10 slots, one task per slot'], ['Works in', 'Worktrees of the workspace repository'], ['Queue', 'Coding queue, held by the Studio API']],
  });
  node('reviewr', 'service', 'review runner', 'host service · 5 slots', 'app', {
    unit: true, slots: 5, queue: 'review',
    volumes: ['repo'],
    netNote: 'loopback to the Studio API', netShort: 'loopback',
    facts: [['Type', 'Host service (not containerised)'], ['Capacity', '5 slots'], ['Queue', 'Review queue, shared with the review VM']],
  });
  node('appdisk', 'disk', 'disk array', '2 disks · RAID 1', 'app', {
    capacity: 480, mirror: true, threshold: 0.8,
    segs: [
      { id: 'sys', name: 'system + images', short: 'system', gb: 58 },
      { id: 'proj', name: 'project data', short: 'project data', gb: 139, data: true },
      { id: 'repo', name: 'workspace repository', short: 'workspace repo', gb: 163, data: true },
      { id: 'snap', name: 'manual snapshot', short: 'snapshot', gb: 43, snapshot: true },
    ],
    facts: [['Layout', '2 disks, RAID 1 (mirror)'], ['Usable', '480 GB'], ['Redundancy', 'Survives one disk failure. A mirror is not a backup.'], ['Alert threshold', '80% full']],
  });

  node('ws', 'machine', 'Workstation', 'Windows · Hyper-V', 'install', {
    headH: 70, hostCpu: true,
    facts: [['Role', 'Hosts the review VM'], ['System', 'Windows with Hyper-V'], ['Reachable', 'Private overlay network only'], ['Location', 'On site']],
  });
  node('vm', 'vm', 'review VM', 'Debian · 12 vCPU · 50 GB', 'ws', {
    headH: 56, cores: 12, memMax: 50, perfHost: true,
    facts: [['Guest', 'Debian'], ['Size', '12 vCPU, 50 GB memory'], ['Runs', '3 review containers and the gate host'], ['Outbound', 'Reverse tunnel to the app host']],
  });
  node('gate', 'container', 'gate host', 'verification suites', 'vm', {
    unit: true, gate: true, queue: 'gate',
    volumes: ['rw'],
    netNote: 'reverse tunnel to the app host', netShort: 'reverse tunnel',
    facts: [['Image', 'gate-host'], ['Runs', 'Verification suites, one batch at a time'], ['Reports', 'Verdicts back through the reverse tunnel'], ['Queue', 'Gate queue, held by the Studio API']],
  });
  node('r1', 'container', 'review-1', '3 slots', 'vm', { unit: true, slots: 3, queue: 'review', volumes: ['rw'], facts: [['Image', 'review-runner'], ['Capacity', '3 slots'], ['Queue', 'Review queue (shared)']] });
  node('r2', 'container', 'review-2', '3 slots', 'vm', { unit: true, slots: 3, queue: 'review', volumes: ['rw'], facts: [['Image', 'review-runner'], ['Capacity', '3 slots'], ['Queue', 'Review queue (shared)']] });
  node('r3', 'container', 'review-3', '2 slots', 'vm', { unit: true, slots: 2, queue: 'review', volumes: ['rw'], facts: [['Image', 'review-runner'], ['Capacity', '2 slots'], ['Queue', 'Review queue (shared)']] });
  node('vmdisk', 'disk', 'virtual disk', 'review workspaces', 'vm', {
    capacity: 200, threshold: 0.8,
    segs: [
      { id: 'vsys', name: 'system + toolchains', short: 'system', gb: 31 },
      { id: 'rw', name: 'review workspaces', short: 'review workspaces', gb: 40, data: true, ephemeral: true },
    ],
    facts: [['Type', 'Virtual disk of the review VM (sample size 200 GB)'], ['Holds', 'Review workspaces, rebuilt per review'], ['Lives on', 'The workstation']],
  });

  node('laptop', 'machine', 'Operator laptop', 'overlay client', 'install', {
    headH: 70, light: true,
    facts: [['Role', 'Operator access to the workstation'], ['Reachable', 'Private overlay network'], ['Monitoring', 'None on this device; link state only']],
  });

  node('net', 'external', 'Public traffic', 'web · webhooks', null, { facts: [['Kind', 'Outside the installation'], ['Enters through', 'The CDN proxy only']] });
  node('cdn', 'external', 'CDN proxy', 'TLS · cache', null, { facts: [['Kind', 'External service, outside the installation'], ['Does', 'Terminates TLS, caches, forwards to the app host'], ['Origin', 'web app on the app host']] });
  node('offsite', 'ghost', 'Off-host backup target', 'not configured', null, { layer: 'backup', facts: [['State', 'Not configured'], ['Missing', 'Encryption key and a target outside the app host'], ['Effect', 'Losing the app host disk array loses all project data']] });

  const links = [
    { id: 'pub', a: 'net', b: 'cdn', kind: 'public', name: 'Public traffic', short: 'HTTPS', proto: 'HTTPS, port 443', dir: 'Requests from outside to the CDN proxy', unit: 'req/s', arrow: true },
    { id: 'origin', a: 'cdn', b: 'web', kind: 'public', name: 'CDN proxy to origin', short: 'origin pull', proto: 'HTTPS, port 443', dir: 'The only public way into the installation', unit: 'req/s', arrow: true },
    { id: 'webapi', a: 'web', b: 'api', kind: 'local', name: 'web app to Studio API', short: 'stack net', proto: 'HTTP on the stack network', dir: 'Calls from the web app', unit: 'req/s', arrow: true },
    { id: 'apicoding', a: 'api', b: 'coding', kind: 'local', name: 'Studio API and coding runner', short: 'loopback', proto: 'HTTP on loopback', dir: 'Runner pulls work, reports results', unit: 'events/min' },
    { id: 'apireview', a: 'api', b: 'reviewr', kind: 'local', name: 'Studio API and review runner', short: 'loopback', proto: 'HTTP on loopback', dir: 'Runner pulls work, reports results', unit: 'events/min' },
    { id: 'tunnel', a: 'gate', b: 'api', kind: 'tunnel', name: 'Reverse tunnel', short: 'reverse tunnel', proto: 'Encrypted tunnel, opened from the review VM', dir: 'Opened outbound by the VM; the app host reaches the gate through it', unit: 'kB/s', arrow: true },
    { id: 'overlay', a: 'laptop', b: 'ws', kind: 'overlay', name: 'Private overlay network', short: 'private overlay', proto: 'Encrypted peer to peer mesh', dir: 'Operator laptop and workstation are peers', unit: 'kB/s' },
    { id: 'backup', a: 'appdisk', b: 'offsite', kind: 'backup', layer: 'backup', name: 'Off-host backup', short: 'no off-host copy', proto: 'Not configured: no key, no target', dir: 'Nothing leaves the app host disk array', broken: true },
  ];
  const linkById = {};
  links.forEach((l) => (linkById[l.id] = l));

  /* ---------- layout ---------- */
  function set(id, x, y, w, h) { const r = nodes[id].rect; r.x = x; r.y = y; r.w = w; r.h = h; }
  function rel(id, parentId, x, y, w, h) { const p = nodes[parentId].rect; set(id, p.x + x, p.y + y, w, h); }
  const side = (id, s, t) => {
    const r = nodes[id].rect; t = t == null ? 0.5 : t;
    if (s === 'l') return [r.x, r.y + r.h * t];
    if (s === 'r') return [r.x + r.w, r.y + r.h * t];
    if (s === 't') return [r.x + r.w * t, r.y];
    return [r.x + r.w * t, r.y + r.h];
  };

  function inner(mode, mw) {
    const s = mw - 48, cw = (s - 48) / 2, cod = Math.round(s * 0.586);
    // App host internals (same in both modes except the order inside the stack)
    rel('stack', 'app', 24, 84, s, 236);
    rel(mode === 'wide' ? 'web' : 'api', 'stack', 16, 56, cw, 164);
    rel(mode === 'wide' ? 'api' : 'web', 'stack', 32 + cw, 56, cw, 164);
    rel('coding', 'app', 24, 352, cod, 220);
    rel('reviewr', 'app', 44 + cod, 352, s - cod - 20, 220);
    rel('appdisk', 'app', 24, 600, s, 124);
    // Workstation internals
    rel('vm', 'ws', 24, 84, s, 460);
    rel('gate', 'vm', 16, 64, cw, 150);
    rel('r1', 'vm', 32 + cw, 64, cw, 150);
    rel('r2', 'vm', 16, 226, cw, 150);
    rel('r3', 'vm', 32 + cw, 226, cw, 150);
    rel('vmdisk', 'vm', 16, 388, s - 32, 56);
  }
  const cy = (id) => nodes[id].rect.y + nodes[id].rect.h / 2;
  const cx = (id) => nodes[id].rect.x + nodes[id].rect.w / 2;
  const bot = (id) => nodes[id].rect.y + nodes[id].rect.h;

  SV.layout = function (mode) {
    let bounds;
    const P = {};
    if (mode === 'wide') {
      set('install', 250, 60, 1290, 868);
      set('app', 290, 130, 560, 748);
      set('ws', 940, 130, 560, 568);
      set('laptop', 1040, 758, 360, 120);
      inner(mode, 560);
      set('cdn', 30, cy('web') - 42, 170, 84);
      set('net', 30, cy('web') - 42 - 160, 170, 84);
      set('offsite', 30, cy('appdisk') - 42, 170, 84);
      P.pub = [side('net', 'b'), side('cdn', 't')];
      P.origin = [side('cdn', 'r'), side('web', 'l')];
      P.webapi = [side('web', 'r'), side('api', 'l')];
      const xc = nodes.api.rect.x + 18;
      P.apicoding = [[xc, bot('api')], [xc, nodes.coding.rect.y]];
      P.apireview = [[cx('reviewr'), bot('api')], [cx('reviewr'), nodes.reviewr.rect.y]];
      P.tunnel = [side('gate', 'l'), [nodes.api.rect.x + nodes.api.rect.w, cy('gate')]];
      P.overlay = [side('laptop', 't'), [cx('laptop'), bot('ws')]];
      P.backup = [side('appdisk', 'l'), side('offsite', 'r')];
      bounds = { x: 10, y: 36, w: 1550, h: 912 };
    } else {
const MW = 780; // wider machines: a phone has width to spare once the plan is stacked
      set('net', 20, 20, 250, 76);
      set('cdn', 320, 20, 250, 76);
      set('offsite', 630, 20, 250, 76);
      set('install', 10, 130, MW + 100, 1622);
      set('app', 60, 186, MW, 748);
      set('ws', 60, 990, MW, 568);
      set('laptop', 60 + (MW - 360) / 2, 1612, 360, 120);
      inner(mode, MW);
      P.pub = [side('net', 'r'), side('cdn', 'l')];
      const wt = side('web', 't'), ct = side('cdn', 'b');
      P.origin = [ct, [ct[0], 158], [wt[0], 158], wt];
      P.webapi = [side('web', 'l'), side('api', 'r')];
      const xc = nodes.api.rect.x + 116;
      P.apicoding = [[xc, bot('api')], [xc, nodes.coding.rect.y]];
      const xa = nodes.api.rect.x + nodes.api.rect.w - 24, ym = bot('stack') + 16;
      P.apireview = [[xa, bot('api')], [xa, ym], [cx('reviewr'), ym], [cx('reviewr'), nodes.reviewr.rect.y]];
      const g = side('gate', 'l'), a = side('api', 'l');
      P.tunnel = [g, [35, g[1]], [35, a[1]], a];
      P.overlay = [side('laptop', 't'), [cx('laptop'), bot('ws')]];
      const d = side('appdisk', 'r'), o = side('offsite', 'b');
      P.backup = [d, [MW + 85, d[1]], [MW + 85, 113], [o[0], 113], o];
      bounds = { x: 0, y: 6, w: MW + 120, h: 1760 };
    }
    links.forEach((l) => (l.pts = P[l.id]));
    links.forEach((l) => {
      let len = 0; l.cum = [0];
      for (let i = 1; i < l.pts.length; i++) { len += Math.hypot(l.pts[i][0] - l.pts[i - 1][0], l.pts[i][1] - l.pts[i - 1][1]); l.cum.push(len); }
      l.len = len;
      l.cross = crossings(l);
    });
    SV.mode = mode;
    SV.bounds = bounds;
    return bounds;
  };

  /* Where a link crosses the frames it passes through (boundary, machine, VM, stack). */
  function ancestors(id) { const out = []; let n = nodes[id]; while (n && n.parent) { n = nodes[n.parent]; out.push(n); } return out; }
  function crossings(l) {
    const A = ancestors(l.a), B = ancestors(l.b);
    const frames = A.filter((n) => B.indexOf(n) < 0).concat(B.filter((n) => A.indexOf(n) < 0));
    const out = [];
    frames.forEach((f) => {
      const r = f.rect;
      for (let i = 1; i < l.pts.length; i++) {
        const p = l.pts[i - 1], q = l.pts[i];
        if (p[1] === q[1]) { // horizontal segment
          const y = p[1]; if (y <= r.y || y >= r.y + r.h) continue;
          [r.x, r.x + r.w].forEach((x) => { if (x > Math.min(p[0], q[0]) && x < Math.max(p[0], q[0])) out.push({ x, y, node: f, horiz: true }); });
        } else { // vertical segment
          const x = p[0]; if (x <= r.x || x >= r.x + r.w) continue;
          [r.y, r.y + r.h].forEach((y) => { if (y > Math.min(p[1], q[1]) && y < Math.max(p[1], q[1])) out.push({ x, y, node: f, horiz: false }); });
        }
      }
    });
    return out;
  }

  SV.nodes = nodes;
  SV.order = order;
  SV.links = links;
  SV.linkById = linkById;
  SV.ancestors = ancestors;
  SV.path = function (id) { return ancestors(id).reverse().concat([nodes[id]]); };
  SV.KIND = { boundary: 'System boundary', machine: 'Machine', vm: 'Virtual machine', stack: 'Container stack', container: 'Container', service: 'Host service', disk: 'Storage', external: 'External', ghost: 'Missing' };
})();
