// Deployment structure renderer for the AGT-3349 Dossier.
// One flat node list, two projections (host view, container view), three
// zoom levels and nested box layout. Plain JavaScript without dependencies;
// build.mjs uses it for the static figures, the prototype uses it live.
(function (root) {
  'use strict';

  const GROUP_KINDS = new Set(['boundary', 'outside', 'location', 'environment', 'host', 'server', 'vm',
    'cluster', 'namespace', 'group', 'registry', 'project', 'scope']);
  const ATTACH = new Set(['host', 'server', 'vm', 'cluster', 'group', 'registry', 'project']);
  const PLACEMENT_KINDS = new Set(['location', 'host', 'server', 'vm', 'cluster', 'namespace']);
  const KIND_LABEL = {
    boundary: 'System boundary', outside: 'Outside the boundary', location: 'Location', environment: 'Environment',
    host: 'Physical machine', server: 'Cloud server', vm: 'Virtual machine', cluster: 'Kubernetes cluster',
    namespace: 'Namespace', group: 'Deployable units', registry: 'Registry', project: 'Project', scope: '',
    container: 'Container', process: 'Process', store: 'Data store', external: 'External service',
    managed: 'Managed service', package: 'Package', artifact: 'Artifact',
  };
  const NOUN = {
    container: ['container', 'containers'], process: ['process', 'processes'], store: ['store', 'stores'],
    external: ['external', 'externals'], managed: ['managed service', 'managed services'],
    package: ['package', 'packages'], artifact: ['artifact', 'artifacts'],
    host: ['host', 'hosts'], server: ['server', 'servers'], vm: ['VM', 'VMs'], cluster: ['cluster', 'clusters'],
    namespace: ['namespace', 'namespaces'], registry: ['registry', 'registries'], project: ['project', 'projects'],
  };
  const STATUS_WORD = { ok: 'Healthy', warn: 'Degraded', down: 'Down', unknown: 'Not observed' };
  const STATUS_RANK = { down: 3, warn: 2, unknown: 1, ok: 0 };
  // Level 1 shows boundaries, locations and environments with collapsed machines.
  // Level 2 opens machines but keeps nested placement (VM, namespace) closed.
  // Level 3 opens everything. A user toggle overrides the level for one node.
  const COLLAPSE_AT = {
    1: new Set(['host', 'server', 'cluster', 'group', 'registry', 'project']),
    2: new Set(['vm', 'namespace']),
    3: new Set(),
  };
  const WRAP = { root: 1500, boundary: 1180, scope: 1180, environment: 1120, location: 1080, outside: 240,
    host: 600, server: 600, vm: 560, cluster: 1120, namespace: 470, group: 560, registry: 420, project: 420 };

  const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const textW = (s, size = 12, weight = 400) => Math.ceil(String(s || '').length * size * (weight >= 600 ? 0.6 : 0.54));
  const plural = (n, kind) => `${n} ${(NOUN[kind] || [kind, kind + 's'])[n === 1 ? 0 : 1]}`;

  // ---------- projection ----------
  function project(scn, opts) {
    const view = opts.view || 'host';
    const byId = new Map(scn.nodes.map(n => [n.id, n]));
    const scope = opts.scope && opts.scope !== 'workspace' ? opts.scope : null;
    const placementLabel = n => {
      const chain = [];
      let p = byId.get(n.parent);
      while (p) { if (PLACEMENT_KINDS.has(p.kind) && p.kind !== 'location' && p.kind !== 'namespace') chain.unshift(p.label); p = byId.get(p.parent); }
      return chain.length ? 'on ' + chain[chain.length - 1] : '';
    };
    const nodes = [];
    for (const n of scn.nodes) {
      if (n.only && n.only !== view) continue;
      if (view === 'container' && PLACEMENT_KINDS.has(n.kind)) continue;
      const copy = { ...n, children: [] };
      if (view === 'container') {
        copy.parentId = n.group !== undefined ? n.group : nearestKept(n, byId);
        if (!GROUP_KINDS.has(n.kind) && !n.keepSub) copy.sub = [placementLabel(n), n.unitSub].filter(Boolean).join(' · ') || n.sub;
      } else copy.parentId = n.parent;
      nodes.push(copy);
    }
    const map = new Map(nodes.map(n => [n.id, n]));
    // Partial picture: keep in-scope nodes, their ancestors, shared externals,
    // and ghost the other projects' nodes that share a placement with them.
    if (scope) {
      const inScope = n => n.project === scope || n.project === '*';
      const keep = new Set();
      const keepUp = id => { let x = map.get(id); while (x && !keep.has(x.id)) { keep.add(x.id); x = map.get(x.parentId); } };
      nodes.filter(inScope).forEach(n => keepUp(n.id));
      // A ghost is another project's object shown only for context.
      const ghostUp = id => { let x = map.get(id); while (x && !keep.has(x.id)) { keep.add(x.id); if (!inScope(x)) x.ghost = true; x = map.get(x.parentId); } };
      for (const n of nodes) {
        if (keep.has(n.id) || GROUP_KINDS.has(n.kind) && n.kind !== 'project') continue;
        const parent = map.get(n.parentId);
        if (parent && keep.has(parent.id) && PLACEMENT_KINDS.has(parent.kind) && n.project) ghostUp(n.id);
      }
      for (const l of scn.links || []) {
        const a = map.get(l.from), b = map.get(l.to);
        // Only the selected project's own objects pull in a neighbour; shared externals do not.
        if (a && b && a.project === scope && !keep.has(b.id) && b.project && !GROUP_KINDS.has(b.kind)) ghostUp(b.id);
        if (a && b && b.project === scope && !keep.has(a.id) && a.project && !GROUP_KINDS.has(a.kind)) ghostUp(a.id);
      }
      for (const id of [...map.keys()]) if (!keep.has(id)) map.delete(id);
    }
    const roots = [];
    for (const n of map.values()) {
      const p = map.get(n.parentId);
      if (p) p.children.push(n); else roots.push(n);
    }
    // Drop empty logical groups and boundaries after projection.
    const prune = list => list.filter(n => {
      n.children = prune(n.children);
      return !(GROUP_KINDS.has(n.kind) && n.children.length === 0 && n.kind !== 'host' && n.kind !== 'server' && n.kind !== 'vm');
    });
    return { roots: prune(roots), map };
  }
  function nearestKept(n, byId) {
    let p = byId.get(n.parent);
    while (p && PLACEMENT_KINDS.has(p.kind)) p = byId.get(p.parent);
    return p ? p.id : undefined;
  }

  // Identical siblings (same rep key) collapse into one node with a count
  // below level 3 unless the user opened their parent explicitly.
  function groupReplicas(node, level, toggles) {
    const open = level >= 3 || toggles[node.id] === false;
    const out = [];
    const seen = new Map();
    for (const c of node.children) {
      groupReplicas(c, level, toggles);
      if (!open && c.rep && !GROUP_KINDS.has(c.kind)) {
        const g = seen.get(c.rep);
        if (g) { g.replicas += c.replicas || 1; g.members.push(c); g.status = worse(g.status, c.status); if (c.drift) g.drift = g.drift || c.drift; if (c.behind) g.behind = true; continue; }
        const copy = { ...c, label: c.repLabel || c.label, replicas: c.replicas || 1, members: [c], id: c.id };
        seen.set(c.rep, copy); out.push(copy); continue;
      }
      out.push(c);
    }
    for (const g of seen.values()) {
      if (g.members.length > 1 && new Set(g.members.map(m => m.sub)).size > 1) g.sub = `${g.members.length} placements`;
    }
    node.children = out;
  }
  const worse = (a, b) => (STATUS_RANK[b] || 0) > (STATUS_RANK[a] || 0) ? b : (a || b);

  function summarise(node) {
    const counts = {};
    const reps = {};
    let worst = node.status;
    let issues = 0;
    let drift = node.drift ? 1 : 0;
    const walk = n => {
      for (const c of n.children) {
        if (GROUP_KINDS.has(c.kind)) { walk(c); if (c.kind === 'vm' || c.kind === 'cluster' || c.kind === 'namespace') counts[c.kind] = (counts[c.kind] || 0) + 1; }
        else {
          const k = c.kind;
          const units = c.members ? c.members.length : 1;
          counts[k] = (counts[k] || 0) + units;
          reps[k] = (reps[k] || 0) + (c.members ? c.replicas : (c.replicas || 1));
          if (c.status === 'warn' || c.status === 'down') issues += 1;
          if (c.drift) drift += 1;
        }
        worst = worse(worst, c.status);
        if (GROUP_KINDS.has(c.kind) && c.drift) drift += 1;
      }
    };
    walk(node);
    return { counts, reps, worst: worst || 'unknown', issues, drift };
  }

  // ---------- layout ----------
  function layout(node, level, toggles, depth) {
    const isGroup = GROUP_KINDS.has(node.kind);
    if (isGroup) {
      const levelCollapsed = level <= 1 ? COLLAPSE_AT[1].has(node.kind) || COLLAPSE_AT[2].has(node.kind)
        : level === 2 ? COLLAPSE_AT[2].has(node.kind) : false;
      const lane = node.kind === 'boundary' || node.kind === 'outside' || node.kind === 'scope' || node.kind === 'environment' || node.kind === 'location';
      node.isCollapsed = toggles[node.id] !== undefined ? toggles[node.id] : lane ? false : levelCollapsed;
    }
    if (isGroup && node.isCollapsed) {
      node.summary = summarise(node);
      const parts = Object.entries(node.summary.counts).map(([k, n]) => plural(n, k)
        + (node.summary.reps[k] > n ? ` (${node.summary.reps[k]} replicas)` : ''));
      node.summaryText = parts.join(' · ') || 'empty';
      const extra = [node.summary.issues ? `${node.summary.issues} need attention` : '', node.summary.drift ? `${node.summary.drift} drift` : ''].filter(Boolean).join(' · ');
      node.summaryExtra = extra;
      const w = Math.max(textW(node.label, 13, 650) + 70, textW(node.summaryText, 11) + 40, Math.ceil(textW(node.kindText(), 10) * 1.25) + 44, extra ? textW(extra, 11) + 40 : 0, 170);
      node.w = Math.min(w, 340); node.h = extra ? 78 : 64;
      return node;
    }
    if (isGroup) {
      const pad = node.kind === 'outside' || node.kind === 'scope' ? 0 : 12;
      const head = node.kind === 'outside' ? 26 : node.kind === 'scope' ? 0 : 46;
      const gap = node.kind === 'scope' ? 30 : node.kind === 'boundary' || node.kind === 'environment' ? 16 : 10;
      const maxW = node.wrap || WRAP[node.kind] || 600;
      node.children.forEach(c => layout(c, level, toggles, depth + 1));
      const rows = [];
      let row = [], rowW = 0;
      const dirCol = node.dir === 'col';
      for (const c of node.children) {
        if (row.length && (dirCol || rowW + gap + c.w > maxW)) { rows.push(row); row = []; rowW = 0; }
        row.push(c); rowW += (row.length > 1 ? gap : 0) + c.w;
      }
      if (row.length) rows.push(row);
      let y = head + (node.kind === 'outside' ? 0 : pad - (head ? 4 : 0));
      if (node.kind === 'scope') y = 0;
      let innerW = 0;
      for (const r of rows) {
        let x = pad;
        const rh = Math.max(...r.map(c => c.h));
        for (const c of r) { c.x = x; c.y = y; x += c.w + gap; }
        innerW = Math.max(innerW, x - gap - pad);
        y += rh + gap;
      }
      const headW = node.kind === 'scope' ? 0 : Math.max(textW(node.label, 13, 650), Math.ceil(textW(node.kindText(), 10) * 1.25)) + 70 + (node.ver ? textW(node.ver, 10) + 26 : 0);
      node.w = Math.max(innerW + pad * 2, headW, 160);
      node.h = Math.max(y - gap + pad, head + 20);
      // Stretch children of a column group to a common width for a calm edge.
      if (dirCol && node.kind !== 'scope') for (const c of node.children) if (!GROUP_KINDS.has(c.kind)) c.w = node.w - pad * 2;
      return node;
    }
    // Leaf
    const sub = node.sub || '';
    const chip = node.ver ? textW(node.ver, 10) + 18 : 0;
    const rep = node.replicas > 1 ? textW('×' + node.replicas, 11, 650) + 16 : 0;
    const labelW = textW(node.label, 12.5, 600) + 34 + rep;
    const subW = textW(sub, 11) + (chip ? chip + 10 : 0) + 22;
    node.w = Math.max(node.kind === 'process' ? 140 : 150, Math.min(300, Math.max(labelW, subW)));
    node.h = node.kind === 'store' || node.kind === 'managed' ? 54 : (sub || chip ? 48 : 34);
    return node;
  }

  function place(node, ox, oy, out, parent) {
    node.ax = ox + (node.x || 0); node.ay = oy + (node.y || 0);
    node.parentNode = parent || null;
    out.set(node.id, node);
    if (node.kind !== 'scope') (out.rects || (out.rects = [])).push(node);
    if (node.members) node.members.forEach(m => out.set(m.id, node));
    if (!node.isCollapsed) for (const c of node.children || []) place(c, node.ax, node.ay, out, node);
    else hideBelow(node, node, out);
  }
  function hideBelow(node, visible, out) {
    for (const c of node.children || []) { out.set(c.id, visible); if (c.members) c.members.forEach(m => out.set(m.id, visible)); hideBelow(c, visible, out); }
  }

  // ---------- drawing ----------
  function glyph(status, x, y) {
    if (!status) return '';
    const t = `<title>${STATUS_WORD[status] || status}</title>`;
    if (status === 'ok') return `<g class="ds-st ds-st-ok" transform="translate(${x} ${y})">${t}<circle r="6"/><path d="M-3 0 L-1 2.4 L3.2 -2.4"/></g>`;
    if (status === 'warn') return `<g class="ds-st ds-st-warn" transform="translate(${x} ${y})">${t}<path class="ds-st-shape" d="M0 -6.5 L6.6 5.2 L-6.6 5.2 Z"/><path d="M0 -2.4 L0 1.2 M0 3 L0 3.3"/></g>`;
    if (status === 'down') return `<g class="ds-st ds-st-down" transform="translate(${x} ${y})">${t}<rect x="-5.6" y="-5.6" width="11.2" height="11.2" rx="1.5"/><path d="M-2.6 -2.6 L2.6 2.6 M2.6 -2.6 L-2.6 2.6"/></g>`;
    return `<g class="ds-st ds-st-unknown" transform="translate(${x} ${y})">${t}<circle r="5.6"/><path d="M-1.8 -1.8 Q0 -4 1.8 -1.8 Q1.8 0 0 0.8 L0 1.6 M0 3 L0 3.2"/></g>`;
  }
  function chip(text, x, y, cls) {
    const w = textW(text, 10) + 12;
    return `<g class="ds-chip ${cls || ''}" transform="translate(${x} ${y})"><rect width="${w}" height="16" rx="8"/><text x="6" y="11.5">${esc(text)}</text></g>`;
  }
  const provClass = n => n.ghost ? ' ds-ghost' : n.prov === 'declared' ? ' ds-declared' : n.prov === 'undeclared' ? ' ds-undeclared' : '';

  function drawNode(n, o, s) {
    const x = n.ax, y = n.ay, w = n.w, h = n.h;
    const showHealth = o.overlay === 'health' || o.overlay === 'all';
    const showVer = o.overlay === 'version' || o.overlay === 'all' || o.overlay === 'health' && o.versions;
    const showDrift = o.overlay === 'drift' || o.overlay === 'all';
    const fade = o.overlay === 'drift' && !(n.drift || n.behind || n.prov === 'declared' || n.prov === 'undeclared' || n.summary && n.summary.drift) ? ' ds-faded' : '';
    const interactive = o.interactive;
    const isGroup = GROUP_KINDS.has(n.kind);
    const label = `${n.label}, ${n.kindText()}${n.status ? ', ' + STATUS_WORD[n.status] : ''}${n.ver ? ', version ' + n.ver : ''}${n.drift ? ', ' + n.drift : ''}${n.ghost ? ', belongs to another project' : ''}`;
    const attrs = interactive
      ? (isGroup && n.kind !== 'scope' && n.kind !== 'outside'
        ? ` tabindex="0" role="button" data-id="${esc(n.id)}" aria-expanded="${!n.isCollapsed}" aria-label="${esc(label + (n.isCollapsed ? ', ' + n.summaryText + '. Press Enter to expand' : '. Press Enter to collapse'))}"`
        : ` tabindex="0" role="button" data-leaf="${esc(n.id)}" aria-label="${esc(label + '. Press Enter for links')}"`)
      : '';
    if (n.kind === 'scope') { (n.children || []).forEach(c => drawNode(c, o, s)); return; }
    if (isGroup && !n.isCollapsed) {
      const cls = `ds-g ds-${n.kind}${provClass(n)}${fade}`;
      s.push(`<g class="${cls}"${attrs}>`);
      if (n.kind === 'outside') {
        s.push(`<text class="ds-outside-label" x="${x}" y="${y + 14}">${esc(n.label)}</text>`);
      } else {
        const rx = n.kind === 'boundary' ? 14 : n.kind === 'environment' || n.kind === 'location' ? 10 : 7;
        s.push(`<rect class="ds-box" x="${x}" y="${y}" width="${w}" height="${h}" rx="${rx}"/>`);
        if (n.kind === 'vm') s.push(`<rect class="ds-inset" x="${x + 3}" y="${y + 3}" width="${w - 6}" height="${h - 6}" rx="${rx - 2}"/>`);
        if (n.kind === 'cluster') s.push(`<path class="ds-inset" d="M${x + 12} ${y + 40} H${x + w - 12}"/>`);
        const lx = x + 12 + (n.kind === 'boundary' ? 0 : 18);
        if (n.kind !== 'boundary' && n.kind !== 'environment' && n.kind !== 'location') s.push(icon(n.kind, x + 12, y + 11));
        s.push(`<text class="ds-glabel" x="${lx}" y="${y + 21}">${esc(n.label)}</text>`);
        s.push(`<text class="ds-kind" x="${lx}" y="${y + 36}">${esc(n.kindText())}</text>`);
        let rx2 = x + w - 12;
        if (showHealth && n.status) { s.push(glyph(n.status, rx2 - 6, y + 17)); rx2 -= 20; }
        if (showVer && n.ver) { const cw = textW(n.ver, 10) + 12; s.push(chip(n.ver, rx2 - cw, y + 9, n.behind ? 'ds-chip-warn' : '')); rx2 -= cw + 6; }
        if (showDrift && n.drift) { const cw = textW(n.drift, 10) + 12; s.push(chip(n.drift, rx2 - cw, y + 9, 'ds-chip-warn')); }
        if (interactive && n.kind !== 'boundary' && n.kind !== 'environment' && n.kind !== 'location') s.push(`<text class="ds-toggle" x="${x + w - 14}" y="${y + h - 8}" text-anchor="end">collapse ▴</text>`);
      }
      s.push('</g>');
      (n.children || []).forEach(c => drawNode(c, o, s));
      return;
    }
    if (isGroup) {
      const sm = n.summary;
      s.push(`<g class="ds-g ds-${n.kind} ds-collapsed${provClass(n)}${fade}"${attrs}>`);
      s.push(`<rect class="ds-box" x="${x}" y="${y}" width="${w}" height="${h}" rx="7"/>`);
      s.push(`<rect class="ds-stack" x="${x + 4}" y="${y + h}" width="${w - 8}" height="3" rx="1.5"/>`);
      s.push(icon(n.kind, x + 12, y + 11));
      s.push(`<text class="ds-glabel" x="${x + 30}" y="${y + 21}">${esc(n.label)}</text>`);
      s.push(`<text class="ds-kind" x="${x + 30}" y="${y + 36}">${esc(n.kindText())}</text>`);
      s.push(`<text class="ds-summary" x="${x + 12}" y="${y + 54}">▸ ${esc(n.summaryText)}</text>`);
      if (n.summaryExtra) s.push(`<text class="ds-summary ds-summary-attn" x="${x + 12}" y="${y + 69}">${esc(n.summaryExtra)}</text>`);
      if (showHealth) s.push(glyph(sm.worst, x + w - 18, y + 17));
      if (showVer && n.ver) { const cw = textW(n.ver, 10) + 12; s.push(chip(n.ver, x + w - 38 - cw, y + 9, n.behind ? 'ds-chip-warn' : '')); }
      if (showDrift && (n.drift || sm.drift)) { const t = n.drift || `${sm.drift} drift`; const cw = textW(t, 10) + 12; s.push(chip(t, x + w - 38 - cw, y + 9, 'ds-chip-warn')); }
      s.push('</g>');
      return;
    }
    // Leaf shapes
    s.push(`<g class="ds-leaf ds-${n.kind}${provClass(n)}${fade}"${attrs}>`);
    if (n.kind === 'process') s.push(`<rect class="ds-shape" x="${x}" y="${y}" width="${w}" height="${h}" rx="${h / 2}"/>`);
    else if (n.kind === 'store' || n.kind === 'managed') {
      const ry = 6;
      s.push(`<path class="ds-shape" d="M${x} ${y + ry} A${w / 2} ${ry} 0 0 1 ${x + w} ${y + ry} V${y + h - ry} A${w / 2} ${ry} 0 0 1 ${x} ${y + h - ry} Z"/>`);
      s.push(`<path class="ds-shape-line" d="M${x} ${y + ry} A${w / 2} ${ry} 0 0 0 ${x + w} ${y + ry}"/>`);
    } else if (n.kind === 'package') {
      s.push(`<rect class="ds-shape" x="${x}" y="${y}" width="${w}" height="${h}" rx="4"/><path class="ds-shape-line" d="M${x} ${y + 9} H${x + w}"/>`);
    } else if (n.kind === 'artifact') {
      s.push(`<path class="ds-shape" d="M${x} ${y} H${x + w - 12} L${x + w} ${y + 12} V${y + h} H${x} Z"/>`);
    } else s.push(`<rect class="ds-shape" x="${x}" y="${y}" width="${w}" height="${h}" rx="${n.kind === 'container' ? 9 : 5}"/>`);
    if (n.replicas > 1) s.push(`<rect class="ds-stack" x="${x + 5}" y="${y + h}" width="${w - 10}" height="3" rx="1.5"/>`);
    const top = n.kind === 'store' || n.kind === 'managed' ? y + 25 : y + (n.sub || (showVer && n.ver) ? 19 : 21);
    const px = n.kind === 'process' ? x + 16 : x + 11;
    s.push(`<text class="ds-label" x="${px}" y="${top}">${esc(n.label)}${n.replicas > 1 ? `<tspan class="ds-rep"> ×${n.replicas}</tspan>` : ''}</text>`);
    let subX = px;
    if (showVer && n.ver) { s.push(chip(n.ver, subX - 2, top + 5, n.behind ? 'ds-chip-warn' : '')); subX += textW(n.ver, 10) + 16; }
    if (n.sub) s.push(`<text class="ds-sub" x="${subX}" y="${top + 16}">${esc(n.sub)}</text>`);
    if (showHealth && n.status) s.push(glyph(n.status, x + w - 13, y + 14));
    if (showDrift && n.drift) { const cw = textW(n.drift, 10) + 12; s.push(chip(n.drift, x + w - cw - 4, y - 9, 'ds-chip-warn')); }
    s.push('</g>');
  }

  function icon(kind, x, y) {
    const t = `transform="translate(${x} ${y})"`;
    switch (kind) {
      case 'host': return `<g class="ds-icon" ${t}><rect x="0" y="0" width="12" height="9" rx="1"/><path d="M3 12 H9 M6 9 V12"/></g>`;
      case 'server': return `<g class="ds-icon" ${t}><rect x="0" y="0" width="12" height="5" rx="1"/><rect x="0" y="6.5" width="12" height="5" rx="1"/></g>`;
      case 'vm': return `<g class="ds-icon" ${t}><rect x="0" y="0" width="12" height="12" rx="2"/><rect x="3" y="3" width="6" height="6" rx="1"/></g>`;
      case 'cluster': return `<g class="ds-icon" ${t}><path d="M6 0 L11.5 3 V9 L6 12 L0.5 9 V3 Z"/></g>`;
      case 'namespace': return `<g class="ds-icon" ${t}><rect x="0" y="0" width="12" height="12" rx="2" stroke-dasharray="2 2"/></g>`;
      case 'registry': return `<g class="ds-icon" ${t}><path d="M0 3 H12 M0 6.5 H12 M0 10 H12"/></g>`;
      case 'project': return `<g class="ds-icon" ${t}><path d="M0 2 H5 L6.5 4 H12 V12 H0 Z"/></g>`;
      case 'group': return `<g class="ds-icon" ${t}><rect x="0" y="0" width="5" height="5" rx="1"/><rect x="7" y="0" width="5" height="5" rx="1"/><rect x="0" y="7" width="5" height="5" rx="1"/><rect x="7" y="7" width="5" height="5" rx="1"/></g>`;
      default: return '';
    }
  }

  function drawLinks(links, vis, o, s) {
    const seen = new Map();
    // Links attach to the nearest visible machine or group box unless the
    // reader selected a node; then that node's links are drawn at leaf level.
    const attach = (n, id) => {
      if (o.linkLevel === 'leaf' || o.focus && (o.focus === id)) return n;
      for (let x = n; x; x = x.parentNode) if (ATTACH.has(x.kind)) return x;
      return n;
    };
    for (const l of links || []) {
      if (o.focus && l.from !== o.focus && l.to !== o.focus && o.focusOnly) continue;
      const va = vis.get(l.from), vb = vis.get(l.to);
      if (!va || !vb) continue;
      const a = attach(va, l.from), b = attach(vb, l.to);
      if (!a || !b || a === b || isAncestor(a, b) || isAncestor(b, a)) continue;
      if (o.linkFilter && !o.linkFilter(l)) continue;
      const key = `${a.id}>${b.id}>${l.kind}`;
      if (seen.has(key)) { seen.get(key).n++; continue; }
      seen.set(key, { l, a, b, n: 1 });
    }
    const labels = [];
    const used = [];
    const placed = [];
    const keys = [];
    for (const { l, a, b, n } of seen.values()) {
      const r = route(a, b, l, vis.rects || [], used);
      used.push(...r.segs);
      const cls = `ds-link ds-link-${l.kind || 'call'}${o.overlay === 'drift' && !l.drift ? ' ds-faded' : ''}`;
      const arrow = l.kind === 'artifact' || l.kind === 'publish' || l.kind === 'call' || l.kind === 'consume';
      s.push(`<path class="ds-link-halo" d="${r.d}"/>`);
      s.push(`<path class="${cls}" d="${r.d}"${arrow ? ' marker-end="url(#ds-arrow)"' : ''}><title>${esc((l.label || l.kind) + ': ' + a.label + ' to ' + b.label)}</title></path>`);
      if (l.label && o.linkLabels !== false) {
        const text = l.label + (n > 1 ? ` ×${n}` : '');
        const key = o.linkKeys !== false;
        const num = String(keys.length + 1);
        const tw = key ? 18 : textW(text, 10.5) + 8;
        const { lx, ly } = placeLabel(r.pts, tw, vis.rects || [], placed);
        placed.push({ l: lx - tw / 2, r: lx + tw / 2, t: ly - 8, b: ly + 7 });
        if (key) {
          keys.push({ num, text, kind: l.kind || 'call', from: a.label, to: b.label });
          labels.push(`<g class="ds-link-key" transform="translate(${lx} ${ly})"><circle r="8.5"/><text y="3.6" text-anchor="middle">${num}</text></g>`);
        } else labels.push(`<g class="ds-link-label"><rect x="${lx - tw / 2}" y="${ly - 8}" width="${tw}" height="15" rx="3"/><text x="${lx}" y="${ly + 3}" text-anchor="middle">${esc(text)}</text></g>`);
      }
    }
    s.push(...labels);
    return keys;
  }
  function isAncestor(a, b) {
    for (const c of a.children || []) { if (c === b || isAncestor(c, b)) return true; }
    return false;
  }
  const chain = n => { const c = []; for (let x = n; x; x = x.parentNode) c.unshift(x); return c; };
  const R_ = n => ({ l: n.ax, t: n.ay, r: n.ax + n.w, b: n.ay + n.h });
  function route(a, b, l, rects, used) {
    const ca = chain(a), cb = chain(b);
    let i = 0; while (i < ca.length && i < cb.length && ca[i] === cb[i]) i++;
    const A = R_(ca[i] || a), B = R_(cb[i] || b);
    const ra = R_(a), rb = R_(b);
    const ancestors = new Set([...ca, ...cb]);
    const off = l.offset || 0;
    const cands = [];
    const midY = (r, other) => Math.max(r.t + 14, Math.min(r.b - 14, (other.t + other.b) / 2 + off));
    const midX = (r, other) => Math.max(r.l + 16, Math.min(r.r - 16, (other.l + other.r) / 2 + off));
    if (B.l >= A.r || A.l >= B.r) {
      const right = B.l >= A.r;
      const x1 = right ? ra.r : ra.l, x2 = right ? rb.l : rb.r;
      const gx = right ? (A.r + B.l) / 2 : (A.l + B.r) / 2;
      const y1 = midY(ra, rb), y2 = midY(rb, ra);
      for (const d of [0, 8, -8]) cands.push([[x1, y1], [gx + off + d, y1], [gx + off + d, y2], [x2, y2]]);
      const ex = right ? A.r + 6 : A.l - 6, en = right ? B.l - 6 : B.r + 6;
      const top = Math.min(A.t, B.t) - 8 + off, bot = Math.max(A.b, B.b) + 8 + off;
      cands.push([[x1, y1], [ex, y1], [ex, top], [en, top], [en, y2], [x2, y2]]);
      cands.push([[x1, y1], [ex, y1], [ex, bot], [en, bot], [en, y2], [x2, y2]]);
      // Approach the target from above or below through its own ancestors.
      const bx = Math.max(rb.l + 16, Math.min(rb.r - 16, (rb.l + rb.r) / 2 + off));
      cands.push([[x1, y1], [ex, y1], [ex, top], [bx, top], [bx, rb.t]]);
      cands.push([[x1, y1], [ex, y1], [ex, bot], [bx, bot], [bx, rb.b]]);
      const ax = Math.max(ra.l + 16, Math.min(ra.r - 16, (ra.l + ra.r) / 2 + off));
      cands.push([[ax, ra.t], [ax, top], [en, top], [en, y2], [x2, y2]]);
      cands.push([[ax, ra.b], [ax, bot], [en, bot], [en, y2], [x2, y2]]);
    }
    if (B.t >= A.b || A.t >= B.b) {
      const down = B.t >= A.b;
      const y1 = down ? ra.b : ra.t, y2 = down ? rb.t : rb.b;
      const gy = down ? (A.b + B.t) / 2 : (A.t + B.b) / 2;
      const x1 = midX(ra, rb), x2 = midX(rb, ra);
      for (const d of [0, 8, -8]) cands.push([[x1, y1], [x1, gy + off + d], [x2, gy + off + d], [x2, y2]]);
      const ey = down ? A.b + 6 : A.t - 6, en = down ? B.t - 6 : B.b + 6;
      const side = Math.max(A.r, B.r) + 8 + off, side2 = Math.min(A.l, B.l) - 8 + off;
      cands.push([[x1, y1], [x1, ey], [side, ey], [side, en], [x2, en], [x2, y2]]);
      cands.push([[x1, y1], [x1, ey], [side2, ey], [side2, en], [x2, en], [x2, y2]]);
      cands.push([[ra.r, midY(ra, rb)], [side, midY(ra, rb)], [side, midY(rb, ra)], [rb.r, midY(rb, ra)]]);
    }
    if (!cands.length) cands.push([[(ra.l + ra.r) / 2, (ra.t + ra.b) / 2], [(rb.l + rb.r) / 2, (rb.t + rb.b) / 2]]);
    let best = null;
    for (const pts of cands) {
      const segs = [];
      for (let k = 1; k < pts.length; k++) segs.push([pts[k - 1], pts[k]]);
      let cost = 0;
      for (const sg of segs) {
        for (const n of rects) if (!ancestors.has(n) && n !== a && n !== b && hits(sg, R_(n))) cost += GROUP_KINDS.has(n.kind) && !n.isCollapsed ? 3 : 10;
        for (const u of used) if (overlap(sg, u)) cost += 1.5;
        cost += (Math.abs(sg[0][0] - sg[1][0]) + Math.abs(sg[0][1] - sg[1][1])) / 600;
      }
      cost += pts.length * 0.8;
      if (!best || cost < best.cost) best = { pts, segs, cost };
    }
    const pts = simplify(best.pts);
    return { d: 'M' + pts.map(p => `${Math.round(p[0])} ${Math.round(p[1])}`).join(' L'), pts, segs: best.segs };
  }
  // Try positions along every segment; prefer spots that cover no box text.
  function placeLabel(pts, tw, rects, placed) {
    let best = null;
    for (let k = 1; k < pts.length; k++) {
      const [p, q] = [pts[k - 1], pts[k]];
      const len = Math.abs(p[0] - q[0]) + Math.abs(p[1] - q[1]);
      if (len < 24) continue;
      for (const f of [0.5, 0.3, 0.7, 0.15, 0.85]) {
        const lx = p[0] + (q[0] - p[0]) * f, ly = p[1] + (q[1] - p[1]) * f;
        const box = { l: lx - tw / 2, r: lx + tw / 2, t: ly - 8, b: ly + 7 };
        let cost = Math.abs(f - 0.5);
        for (const n of rects) {
          const r = R_(n);
          const inter = box.r > r.l && box.l < r.r && box.b > r.t && box.t < r.b;
          if (!inter) continue;
          const contained = box.l >= r.l && box.r <= r.r && box.t >= r.t && box.b <= r.b;
          if (!GROUP_KINDS.has(n.kind) || n.isCollapsed) cost += 20;
          else if (!contained) cost += 2;
          else if (box.t < r.t + 44) cost += 6;
        }
        for (const b of placed) if (box.r > b.l && box.l < b.r && box.b > b.t && box.t < b.b) cost += 15;
        if (!best || cost < best.cost) best = { lx, ly, cost };
      }
    }
    if (!best) { const p = pts[0], q = pts[pts.length - 1]; return { lx: (p[0] + q[0]) / 2, ly: (p[1] + q[1]) / 2 }; }
    return best;
  }
  function simplify(pts) {
    const out = [pts[0]];
    for (let k = 1; k < pts.length; k++) {
      const p = pts[k], q = out[out.length - 1];
      if (Math.abs(p[0] - q[0]) < 0.5 && Math.abs(p[1] - q[1]) < 0.5) continue;
      out.push(p);
    }
    return out;
  }
  function hits([p, q], r) {
    const l = Math.min(p[0], q[0]), rr = Math.max(p[0], q[0]), t = Math.min(p[1], q[1]), bb = Math.max(p[1], q[1]);
    return rr > r.l + 1 && l < r.r - 1 && bb > r.t + 1 && t < r.b - 1;
  }
  function overlap([p, q], [u, v]) {
    const horiz = Math.abs(p[1] - q[1]) < 0.5, uh = Math.abs(u[1] - v[1]) < 0.5;
    if (horiz && uh && Math.abs(p[1] - u[1]) < 4) return Math.max(p[0], q[0]) > Math.min(u[0], v[0]) && Math.min(p[0], q[0]) < Math.max(u[0], v[0]);
    if (!horiz && !uh && Math.abs(p[0] - u[0]) < 4) return Math.max(p[1], q[1]) > Math.min(u[1], v[1]) && Math.min(p[1], q[1]) < Math.max(u[1], v[1]);
    return false;
  }

  function render(scn, opts) {
    const o = Object.assign({ view: 'host', level: 3, overlay: 'health', toggles: {}, interactive: false, idPrefix: 'f' }, opts || {});
    const { roots } = project(scn, o);
    const top = { id: '__root', kind: 'scope', label: '', children: roots, dir: scn.rootDir || 'row', wrap: scn.rootWrap || WRAP.root };
    const kt = n => () => n.kindLabel !== undefined ? n.kindLabel : [KIND_LABEL[n.kind], n.kindSub].filter(Boolean).join(' · ');
    const decorate = n => { n.kindText = kt(n); (n.children || []).forEach(decorate); };
    decorate(top);
    top.children.forEach(c => groupReplicas(c, o.level, o.toggles));
    layoutRoot(top, o.level, o.toggles);
    const vis = new Map();
    place(top, 0, 0, vis);
    const M = 14;
    const s = [];
    s.push(`<g transform="translate(${M} ${M})">`);
    drawNode(top, o, s);
    const keys = drawLinks(scn.links, vis, o, s) || [];
    // Link key: one row per numbered link, wrapped to the drawing width.
    let legendH = 0;
    if (keys.length) {
      const maxW = Math.max(top.w, 520);
      let x = 0, y = top.h + 26;
      for (const k of keys) {
        const text = `${k.text} · ${k.from} → ${k.to}`;
        const w = textW(text, 11) + 70;
        if (x > 0 && x + w > maxW) { x = 0; y += 22; }
        s.push(`<g class="ds-key-item" transform="translate(${x} ${y})"><g class="ds-link-key" transform="translate(9 0)"><circle r="8.5"/><text y="3.6" text-anchor="middle">${k.num}</text></g><path class="ds-link ds-link-${k.kind}" d="M22 0 H46"/><text class="ds-key-text" x="52" y="4">${esc(text)}</text></g>`);
        x += w + 14;
      }
      legendH = y - top.h + 14;
    }
    s.push('</g>');
    const W = Math.ceil(top.w + M * 2), H = Math.ceil(top.h + legendH + M * 2);
    const title = o.title || scn.title;
    const desc = o.desc || scn.desc || '';
    const tid = `${o.idPrefix}-t`, did = `${o.idPrefix}-d`;
    const svg = `<svg class="ds-svg" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="${o.interactive ? 'group' : 'img'}" aria-labelledby="${tid} ${did}"><title id="${tid}">${esc(title)}</title><desc id="${did}">${esc(desc)}</desc>${s.join('')}</svg>`;
    return { svg, width: W, height: H, tree: top };
  }
  function layoutRoot(top, level, toggles) {
    top.children.forEach(c => layout(c, level, toggles, 1));
    const gap = 30;
    let x = 0, y = 0, rowH = 0, maxW = 0;
    for (const c of top.children) {
      if (x > 0 && (top.dir === 'col' || x + c.w > top.wrap)) { x = 0; y += rowH + gap; rowH = 0; }
      c.x = x; c.y = y; x += c.w + gap; rowH = Math.max(rowH, c.h); maxW = Math.max(maxW, x - gap);
    }
    top.w = maxW; top.h = y + rowH;
  }

  // Accessible outline of the same projection, used beside the prototype.
  function outline(tree) {
    const li = n => {
      const kids = n.isCollapsed ? '' : (n.children || []).map(li).join('');
      const bits = [n.kindText(), n.status ? STATUS_WORD[n.status] : '', n.ver ? 'version ' + n.ver : '', n.drift || '', n.replicas > 1 ? '×' + n.replicas : '', n.isCollapsed ? n.summaryText : '', n.ghost ? 'other project' : ''].filter(Boolean).join(' · ');
      return `<li><b>${esc(n.label)}</b> <span>${esc(bits)}</span>${kids ? `<ul>${kids}</ul>` : ''}</li>`;
    };
    return `<ul>${(tree.children || []).map(li).join('')}</ul>`;
  }

  const api = { render, outline, STATUS_WORD, KIND_LABEL };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  root.DSV = api;
})(typeof globalThis !== 'undefined' ? globalThis : this);
