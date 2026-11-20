// Layers for the live structure canvas: each layer names its data, its
// encoding and its legend, and draws through the engine hooks. Layers
// combine: badges sit in the top-right corner, metric rows at the bottom of a
// card, outline and tint carry state. Colour stays in the accents.
(function (root) {
  'use strict';

  const LAYERS = [
    { id: 'perf', title: 'Performance and network',
      data: 'CPU, memory, load and slots per node (host telemetry); link state, latency and traffic (link supervisor, host counters).',
      encoding: 'Gauges in each card; amber from 75 %, red from 90 %. Link width and particle density follow traffic; dash pattern names the link type; latency on the link.',
      legend: [['bar', 'muted', 'CPU and memory gauge'], ['bar', 'warn', '75 % and more'], ['bar', 'bad', '90 % and more'], ['dots', 'muted', 'Traffic: denser is more'],
        ['line', 'muted', 'Private network', []], ['line', 'muted', 'Tunnel', [10, 5]], ['line', 'muted', 'Forwarder', [3, 3]], ['line', 'muted', 'CDN proxy', [1.5, 3.5]], ['line', 'muted', 'HTTPS to outside', [7, 3, 1.5, 3]], ['broken', 'bad', 'Link down']] },
    { id: 'storage', title: 'Storage',
      data: 'Volumes and mounts per machine with size, used, free and growth (host filesystems, telemetry history).',
      encoding: 'A volume bar under each machine header split by mount; amber below 30 GB or 15 % free, red below 8 GB or 5 %; days until full from the growth trend.',
      legend: [['bar', 'muted', 'Used by a mount'], ['bar', 'warn', 'Disk pressure'], ['bar', 'bad', 'Critical'], ['text', 'muted', 'Full in N days']] },
    { id: 'backup', title: 'Backup',
      data: 'Every stateful data location with its class, its copies, the last successful copy and the recovery point (/api/operator/backups, data locations).',
      encoding: 'A shield per node: red cross for a must-keep location without an off-host copy, amber for a local copy only, green tick for a verified off-host copy, outline for reproducible, faint for cache. Copy links to the git remote and the backup target.',
      legend: [['shield', 'bad', 'No off-host copy'], ['shield', 'warn', 'Local copy only'], ['shield', 'good', 'Verified off-host copy'], ['shield', 'muted', 'Reproducible'], ['shield', 'faint', 'Cache'], ['line', 'good', 'Copy to a remote', [2, 4]], ['line', 'bad', 'Backup job missing', [6, 4]]] },
    { id: 'warnings', title: 'Construction sites and warnings',
      data: 'Open problems pinned to their node: missing or behind toolchains, releases behind stable, links down, parked or refused cards, disk pressure, expiring credentials, missing backups.',
      encoding: 'A cone per node with the count, red for blocking, amber for degrading; the top problem is written beside it when there is room. The side list says what blocks and the next step.',
      legend: [['pin', 'bad', 'Blocks work or recovery'], ['pin', 'warn', 'Degrades'], ['pin', 'muted', 'Information']] },
    { id: 'health', title: 'Health',
      data: 'Observed health per node and link (host reports, probes, link supervisor).',
      encoding: 'A dot in the corner and a soft tint for degraded or down nodes; hollow dot when not observed.',
      legend: [['dot', 'good', 'Healthy'], ['dot', 'warn', 'Degraded'], ['dot', 'bad', 'Down'], ['ring', 'faint', 'Not observed']] },
    { id: 'version', title: 'Version',
      data: 'Running release per node against the stable channel; pending host installs (host releases).',
      encoding: 'A release chip; amber with the distance when behind stable.',
      legend: [['chip', 'muted', 'On stable'], ['chip', 'warn', 'Behind stable']] },
    { id: 'drift', title: 'Drift',
      data: 'Declared placement against observed state (placement file, host reports).',
      encoding: 'Dashed outline and a chip: undeclared, declared only, differs.',
      legend: [['outline', 'warn', 'Undeclared', [5, 3]], ['outline', 'faint', 'Declared only', [2, 3]], ['outline', 'warn', 'Differs']] },
  ];

  const fmtBytes = b => (b >= 1e6 ? (b / 1e6).toFixed(1) + ' MB/s' : b >= 1e3 ? Math.round(b / 1e3) + ' kB/s' : b > 0 ? Math.round(b) + ' B/s' : 'idle');
  const gb = v => (v == null ? 'size not reported' : v >= 10 ? Math.round(v) + ' GB' : v >= 1 ? v.toFixed(1) + ' GB' : v > 0 ? Math.round(v * 1024) + ' MB' : 'under 1 MB');

  // Volume pressure as HostDiskPressurePolicy defines it.
  function pressure(v) {
    const free = v.sizeGb * (1 - v.usedPct / 100), freePct = 100 - v.usedPct;
    if (free < 8 || freePct < 5) return 'bad';
    if (free < 30 || freePct < 15) return 'warn';
    return 'ok';
  }
  function daysToFull(v) {
    const free = v.sizeGb * (1 - v.usedPct / 100);
    return v.growthGbPerDay > 0 ? Math.round(free / v.growthGbPerDay) : null;
  }

  // Backup status of one data location.
  function locStatus(l) {
    if (l.cls === 'cache') return 'cache';
    if (l.cls === 'reproducible') return 'repro';
    const off = l.copies.filter(c => c.offHost);
    if (off.length && off.every(c => c.verified)) return 'ok';
    if (l.copies.length) return 'local';
    return 'gap';
  }
  const STATUS_RANK = { gap: 4, local: 3, ok: 2, repro: 1, cache: 0 };
  const STATUS_TONE = { gap: 'bad', local: 'warn', ok: 'good', repro: 'muted', cache: 'faint' };
  const STATUS_WORD = { gap: 'no off-host copy', local: 'local copy only', ok: 'verified off-host copy', repro: 'reproducible', cache: 'cache' };

  function createPainter(state) {
    const T = () => state.T;
    let idx = null;
    // Displayed values ease toward their facts so a delta reads as change.
    const shown = new Map();
    let animating = false;
    const easing = new Set();
    function eased(key, target) {
      if (state.motion === 'off' || !Number.isFinite(target)) { shown.set(key, target); return target; }
      const prev = shown.get(key);
      const cur = Number.isFinite(prev) ? prev : target;
      const next = Math.abs(target - cur) < 0.15 ? target : cur + (target - cur) * 0.18;
      if (next !== target) { animating = true; easing.add(key); }
      shown.set(key, next);
      return next;
    }

    function reindex() {
      const sc = state.scene;
      const locs = new Map(), warns = new Map(), vols = new Map();
      for (const l of state.facts.dataLocations) { if (!locs.has(l.node)) locs.set(l.node, []); locs.get(l.node).push(l); }
      for (const w of state.facts.warnings) { if (!warns.has(w.node)) warns.set(w.node, []); warns.get(w.node).push(w); }
      for (const [m, vs] of Object.entries(state.facts.volumes)) vols.set(m, vs);
      // Roll-ups for closed groups: everything below a node.
      const below = new Map();
      const roll = n => {
        const r = { locs: [...(locs.get(n.id) || [])], warns: [...(warns.get(n.id) || [])], leaves: n.children.length ? 0 : 1, kinds: {} };
        for (const c of n.children) {
          const cr = roll(c);
          r.locs.push(...cr.locs); r.warns.push(...cr.warns); r.leaves += cr.leaves;
          r.kinds[c.kind] = (r.kinds[c.kind] || 0) + 1;
          for (const [k, v] of Object.entries(cr.kinds)) r.kinds[k] = (r.kinds[k] || 0) + v;
        }
        below.set(n.id, r);
        return r;
      };
      for (const c of sc.root.children) roll(c);
      idx = { locs, warns, vols, below };
    }

    function backupOf(n, rolled) {
      const ls = rolled ? idx.below.get(n.id).locs : idx.locs.get(n.id) || [];
      if (!ls.length) return null;
      let worst = 'cache';
      for (const l of ls) { const s = locStatus(l); if (STATUS_RANK[s] > STATUS_RANK[worst]) worst = s; }
      const must = ls.filter(l => l.cls === 'must' || l.cls === 'escrow');
      return { worst, locs: ls, must: must.length, copied: must.filter(l => locStatus(l) === 'ok').length };
    }
    function warningsOf(n, rolled) {
      const ws = rolled ? idx.below.get(n.id).warns : idx.warns.get(n.id) || [];
      const rank = state.spec && state.spec.ranking ? state.spec.ranking : null;
      const order = w => (rank && rank.includes(w.id) ? rank.indexOf(w.id) : 100) + (w.severity === 'bad' ? 0 : w.severity === 'warn' ? 0.5 : 0.8);
      return [...ws].sort((a, b) => order(a) - order(b));
    }
    const ob = n => state.facts.obs[n.id] || {};
    const on = id => state.layers.has(id);
    const tone = t => ({ bad: T().bad, warn: T().warn, good: T().good, muted: T().muted, faint: T().faint, link: T().link, info: T().link }[t] || T().muted);
    const sev = v => (v >= 90 ? 'bad' : v >= 75 ? 'warn' : 'muted');

    // ---------- glyphs ----------
    function shield(ctx, x, y, s, status) {
      const c = tone(STATUS_TONE[status]);
      ctx.save();
      ctx.beginPath();
      ctx.moveTo(x, y - s); ctx.lineTo(x + s * 0.82, y - s * 0.62); ctx.lineTo(x + s * 0.82, y + s * 0.05);
      ctx.quadraticCurveTo(x + s * 0.7, y + s * 0.75, x, y + s); ctx.quadraticCurveTo(x - s * 0.7, y + s * 0.75, x - s * 0.82, y + s * 0.05);
      ctx.lineTo(x - s * 0.82, y - s * 0.62); ctx.closePath();
      if (status === 'repro' || status === 'cache') { ctx.strokeStyle = c; ctx.lineWidth = 1.2; if (status === 'cache') ctx.setLineDash([2, 2]); ctx.stroke(); }
      else { ctx.fillStyle = c; ctx.fill(); }
      ctx.setLineDash([]);
      ctx.strokeStyle = T().paper; ctx.lineWidth = Math.max(1.2, s * 0.22); ctx.lineCap = 'round';
      ctx.beginPath();
      const u = s * 0.38;
      if (status === 'gap') { ctx.moveTo(x - u, y - u); ctx.lineTo(x + u, y + u); ctx.moveTo(x + u, y - u); ctx.lineTo(x - u, y + u); }
      else if (status === 'ok') { ctx.moveTo(x - u, y); ctx.lineTo(x - u * 0.2, y + u * 0.8); ctx.lineTo(x + u, y - u * 0.7); }
      else if (status === 'local') { ctx.moveTo(x, y - u * 1.1); ctx.lineTo(x, y + u * 0.3); ctx.moveTo(x, y + u * 0.95); ctx.lineTo(x, y + u); }
      ctx.stroke();
      ctx.restore();
    }
    function cone(ctx, x, y, s, t, count) {
      // A construction cone: a rounded triangle with one light stripe.
      ctx.save();
      ctx.fillStyle = tone(t);
      ctx.beginPath(); ctx.moveTo(x, y - s); ctx.lineTo(x + s * 0.8, y + s * 0.7); ctx.lineTo(x - s * 0.8, y + s * 0.7); ctx.closePath(); ctx.fill();
      ctx.fillRect(x - s, y + s * 0.62, s * 2, s * 0.32);
      ctx.fillStyle = T().paper; ctx.fillRect(x - s * 0.42, y - s * 0.05, s * 0.84, s * 0.22);
      if (count > 1) {
        const r = Math.max(6, s * 0.62);
        ctx.fillStyle = T().ink; ctx.beginPath(); ctx.arc(x + s * 0.95, y - s * 0.7, r, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = T().paper; ctx.font = '700 ' + Math.round(r * 1.25) + 'px ' + (T().font || 'system-ui'); ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ctx.fillText(String(count), x + s * 0.95, y - s * 0.66);
      }
      ctx.restore();
    }
    function dot(ctx, x, y, r, t, hollow) {
      ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
      if (hollow) { ctx.strokeStyle = tone(t); ctx.lineWidth = 1.4; ctx.stroke(); } else { ctx.fillStyle = tone(t); ctx.fill(); }
    }
    function bar(ctx, x, y, w, h, frac, t) {
      ctx.fillStyle = T().surfaceStrong; ctx.fillRect(x, y, w, h);
      ctx.fillStyle = tone(t); ctx.fillRect(x, y, Math.max(0, Math.min(1, frac)) * w, h);
    }
    function chip(api, x, y, str, t, size) {
      const ctx = api.ctx, k = api.st.k, fs = Math.max((size || 8.5) * k, 8.6);
      ctx.font = '600 ' + fs.toFixed(1) + 'px ' + (T().font || 'system-ui');
      const w = ctx.measureText(str).width + fs * 1.1, h = fs * 1.55;
      ctx.fillStyle = t === 'warn' ? T().warnSoft : t === 'bad' ? T().badSoft : t === 'good' ? T().goodSoft : T().surfaceStrong;
      ctx.beginPath(); ctx.roundRect ? ctx.roundRect(x, y - h * 0.75, w, h, h / 2) : ctx.rect(x, y - h * 0.75, w, h); ctx.fill();
      ctx.fillStyle = t === 'warn' ? T().warn : t === 'bad' ? T().bad : t === 'good' ? T().good : T().muted;
      ctx.textAlign = 'left'; ctx.textBaseline = 'alphabetic'; ctx.fillText(str, x + fs * 0.55, y + fs * 0.08);
      return w;
    }

    // ---------- hooks ----------
    function nodeStyle(n) {
      const o = ob(n), t = T(), out = {};
      const leafLike = !n.children.length || n.open < 0.5;
      if (n.ghost) { out.dash = [4, 4]; out.stroke = on('backup') ? t.bad : t.faint; out.fill = t.paper; }
      if (on('drift') && o.difference) {
        if (o.difference === 'undeclared') { out.dash = [5, 3]; out.stroke = t.warn; }
        else if (o.difference === 'declared-only') { out.dash = [2, 3]; out.stroke = t.faint; }
        else if (o.difference === 'differs') out.stroke = t.warn;
      }
      if (leafLike && on('health')) {
        if (o.health === 'warn') out.fill = t.warnSoft;
        else if (o.health === 'bad' || o.health === 'down') out.fill = t.badSoft;
      }
      if (leafLike && on('perf') && !out.fill && o.cpu != null) {
        const c = eased(n.id + ':cpu', o.cpu);
        if (c >= 90) out.fill = t.badSoft; else if (c >= 75) out.fill = t.warnSoft;
      }
      if (state.selectedId === n.id) { out.stroke = t.link; out.lineWidth = 2.4; out.dash = null; }
      return out;
    }

    // Iso heights: leaves rise with the emphasised metric.
    function height(n) {
      if (n.children.length || !n.th) return n.th;
      const o = ob(n);
      if (on('perf') && o.cpu != null) return 10 + eased(n.id + ':h', o.cpu) * 0.55;
      if (on('storage')) { const m = (state.facts.volumes[n.parent] || [])[0]; const mt = m && m.mounts.find(x => x.node === n.id); if (mt) return 10 + Math.min(60, mt.gb * 1.2); }
      return n.th;
    }

    function decorate(api, n, top, info) {
      const ctx = api.ctx, k = api.st.k, t = T();
      const P = (ox, oy) => api.toScreen(n.x + ox, n.y + oy, n.z + (n.hz == null ? n.th : n.hz));
      const group = info.group;
      const rolled = group && n.open < 0.5;
      const iso = api.proj.m > 0.5;
      const bs = Math.max(5, Math.min(13, 8.5 * k));
      // Top-right badge strip: health dot, backup shield, warning cone (right to left).
      let bx = P(n.w - 16, 0)[0], by = P(n.w - 16, group ? 22 : 20)[1];
      if (n.kind === 'site' || n.kind === 'outside') by = P(0, 26)[1];
      const o = ob(n);
      if (on('health') && (o.health || !group) && n.kind !== 'site' && n.kind !== 'outside') {
        dot(ctx, bx, by, Math.max(3, bs * 0.5), o.health === 'ok' ? 'good' : o.health === 'warn' ? 'warn' : o.health === 'bad' || o.health === 'down' ? 'bad' : 'faint', !o.health || o.health === 'unknown');
        bx -= bs * 2.2;
      }
      const b = backupOf(n, rolled);
      if (b && (on('backup') || b.worst === 'gap' || b.worst === 'local')) {
        // Missing backup stays visible in every layer combination.
        if (on('backup') || b.worst === 'gap') { shield(ctx, bx, by, on('backup') ? bs : bs * 0.8, b.worst); bx -= bs * 2.6; }
      }
      const ws = warningsOf(n, rolled);
      if (on('warnings') && ws.length) {
        const worst = ws.some(w => w.severity === 'bad') ? 'bad' : ws.some(w => w.severity === 'warn') ? 'warn' : 'muted';
        const cs = bs * 1.05;
        cone(ctx, bx, by, cs, worst, ws.length);
        // The top problem in words when there is room.
        if (!iso && k > 0.6 && !group && n.open < 0.5) {
          const [lx, ly] = P(14, n.h - 10);
          api.text(ws[0].title, lx, ly, 8.6, worst === 'bad' ? t.bad : t.warn, 650, 'left', n.w - 28, 9);
        }
        bx -= cs * 2.8;
      }
      if (iso) return;
      if (group) decorateGroup(api, n, P, info, rolled);
      else decorateLeaf(api, n, P, ws);
    }

    function decorateGroup(api, n, P, info, rolled) {
      const ctx = api.ctx, t = T(), k = api.st.k, o = ob(n);
      const vols = idx.vols.get(n.id) || [];
      const pad = n.kind === 'vm' ? 16 : 20;
      if (!rolled && (n.kind === 'machine' || n.kind === 'vm')) {
        // Open machine: metrics on the header line, volume bar under the header.
        let rx = P(n.w - pad - 64, 0)[0];
        const [, hy] = P(0, 30);
        if (on('perf') && o.cpu != null && k >= 0.75) {
          const cpu = eased(n.id + ':cpu', o.cpu), memPct = o.mem ? o.mem[0] / o.mem[1] * 100 : null;
          const str = 'CPU ' + Math.round(cpu) + ' %' + (o.load != null ? ' · load ' + o.load.toFixed(1) + ' of ' + o.cores : '') + (memPct != null ? ' · memory ' + Math.round(memPct) + ' %' : '');
          api.text(str, rx, hy, 9, cpu >= 75 ? t.warn : t.muted, 500, 'right', null, 9.5);
        }
        if (on('storage') && vols.length) {
          const v = vols[0], pr = pressure(v), used = eased(n.id + ':' + v.id, v.usedPct);
          // Between the header text and the first child row, in screen space.
          const [x0, top] = P(pad, 0), [x1] = P(n.w - pad, 0);
          const w = x1 - x0, h = Math.max(3, 4.5 * k);
          const childTop = top + ((n.kind === 'vm' ? 16 + 38 : 20 + 46) * k);
          const yb = Math.min(top + (n.headPx || 38) + 1, childTop - h - 2);
          drawVolume(ctx, x0, yb, w, h, v, used, pr);
          const d = daysToFull({ ...v, usedPct: used });
          const label = v.label + ' · ' + Math.round(used) + ' % of ' + v.sizeGb + ' GB' + (d != null ? ' · full in about ' + d + ' days' : '');
          if (k >= 0.85) api.text(label, x1, P(0, n.kind === 'vm' ? 40 : 46)[1], 8.5, pr === 'ok' ? t.muted : pr === 'bad' ? t.bad : t.warn, pr === 'ok' ? 400 : 650, 'right', null, 9);
        }
        return;
      }
      if (!rolled) return;
      // Closed group: a summary of what is inside, per active layer.
      ctx.save();
      ctx.globalAlpha *= info.summaryAlpha;
      const r = idx.below.get(n.id);
      const lines = [];
      const kinds = r.kinds;
      const parts = [];
      if (kinds.machine) parts.push(kinds.machine + (kinds.machine === 1 ? ' machine' : ' machines'));
      if (kinds.vm) parts.push(kinds.vm + (kinds.vm === 1 ? ' VM' : ' VMs'));
      if (kinds.container) parts.push(kinds.container + (kinds.container === 1 ? ' container' : ' containers'));
      if (kinds.external) parts.push(kinds.external + ' services');
      lines.push([parts.join(' · ') || ' ', t.ink, 600]);
      if (on('perf') && o.cpu != null) lines.push(['CPU ' + Math.round(eased(n.id + ':cpu', o.cpu)) + ' %' + (o.mem ? ' · memory ' + Math.round(o.mem[0] / o.mem[1] * 100) + ' %' : ''), o.cpu >= 75 ? t.warn : t.muted, 400]);
      if (on('storage') && vols.length) { const v = vols[0], pr = pressure(v); const d = daysToFull(v); lines.push([v.label + ' ' + Math.round(v.usedPct) + ' %' + (d ? ' · full in about ' + d + ' days' : ''), pr === 'ok' ? t.muted : t.warn, pr === 'ok' ? 400 : 650]); }
      const bk = backupOf(n, true);
      if (bk && bk.must) lines.push([(bk.must - bk.copied) + ' of ' + bk.must + ' must-keep locations without off-host copy', bk.must > bk.copied ? t.bad : t.good, 650]);
      if (on('warnings') && r.warns.length) lines.push([r.warns.length + (r.warns.length === 1 ? ' open problem' : ' open problems'), t.warn, 600]);
      const isSite = n.kind === 'site' || n.kind === 'outside';
      const q = n.q, bottom = q[3][1] - 6;
      const fsPx = Math.max((isSite ? 12 : 11) * k, isSite ? 10.5 : 10);
      let y = q[0][1] + (n.headPx || 40) + fsPx * 1.5;
      const [lx] = P(isSite ? 30 : 20, 0);
      for (const [s2, c, w] of lines) {
        if (y > bottom) break;
        api.text(s2, lx, y, isSite ? 12 : 11, c, w, 'left', n.w - 40, isSite ? 10.5 : 10);
        y += Math.max(17 * k, fsPx * 1.4);
      }
      if (on('storage') && vols.length && !isSite && y - fsPx * 0.6 < bottom) {
        const v = vols[0];
        const [x0] = P(20, 0), [x1] = P(n.w - 20, 0);
        drawVolume(ctx, x0, y - fsPx * 0.7, x1 - x0, Math.max(3, 5 * k), v, v.usedPct, pressure(v));
      }
      ctx.restore();
    }

    function drawVolume(ctx, x, y, w, h, v, usedPct, pr) {
      const t = T();
      ctx.fillStyle = t.surfaceStrong; ctx.fillRect(x, y, w, h);
      // Mount segments in neutral steps with a 2 px gap; the rest of used space in the pressure tone.
      let cx = x;
      const total = v.sizeGb;
      for (const m of v.mounts) {
        const mw = Math.max(1, m.gb / total * w - 2);
        ctx.fillStyle = t.muted; ctx.globalAlpha *= 0.55; ctx.fillRect(cx, y, mw, h); ctx.globalAlpha /= 0.55;
        cx += mw + 2;
      }
      const usedW = usedPct / 100 * w;
      if (usedW > cx - x) { ctx.fillStyle = pr === 'ok' ? t.faint : pr === 'bad' ? t.bad : t.warn; ctx.fillRect(cx, y, usedW - (cx - x), h); }
    }

    function decorateLeaf(api, n, P, ws) {
      const ctx = api.ctx, t = T(), k = api.st.k, o = ob(n);
      const open = n.kind === 'container' ? n.open : 0;
      const rowsAlpha = 1 - open;
      const base = ctx.globalAlpha;
      // Metric rows need room: they appear from zoom 0.6 on.
      if (rowsAlpha > 0.02 && k >= 0.6) {
        ctx.globalAlpha = base * rowsAlpha * Math.min(1, (k - 0.6) / 0.15);
        let y = 64;
        const row = () => { const r = y; y += 16; return r; };
        const room = () => y < n.h - (on('warnings') && ws.length && k > 0.55 ? 20 : 4);
        if (on('perf') && o.cpu != null && room()) {
          const ry = row();
          const cpu = eased(n.id + ':cpu', o.cpu);
          const [x0, yy] = P(14, ry);
          api.text('CPU', x0, yy + 3 * k, 8, t.faint, 600, 'left', 30, 8);
          const [bx0] = P(36, 0), [bx1] = P(84, 0);
          bar(ctx, bx0, yy - 3.5 * k, bx1 - bx0, Math.max(2, 4.5 * k), cpu / 100, sev(cpu));
          api.text(Math.round(cpu) + ' %', P(88, 0)[0], yy + 3 * k, 8.2, cpu >= 75 ? t.warn : t.muted, 600, 'left', 40, 8.8);
          if (o.slots) {
            const [sx, sy] = P(n.w - 16, ry);
            for (let i = 0; i < o.slots[1]; i++) dot(ctx, sx - i * 9 * k, sy, Math.max(2, 3 * k), 'muted', i >= o.slots[0]);
          }
        }
        if (on('storage') && room()) {
          const v = (state.facts.volumes[n.parent] || state.facts.volumes[n.parentNode && n.parentNode.parent] || [])[0];
          const mts = v ? v.mounts.filter(m => m.node === n.id) : [];
          if (mts.length) { const [x0, yy] = P(14, row()); api.text(mts.map(m => m.path + ' ' + gb(m.gb)).join(' · '), x0, yy + 3 * k, 8.2, t.muted, 500, 'left', n.w - 28, 8.8); }
        }
        if (on('backup') && room()) {
          const b = backupOf(n, false);
          if (b) {
            const [x0, yy] = P(14, row());
            const str = b.must ? (b.must - b.copied) + ' of ' + b.must + ' must-keep without copy' : STATUS_WORD[b.worst];
            api.text(str, x0, yy + 3 * k, 8.2, b.must > b.copied ? t.bad : t.muted, b.must > b.copied ? 650 : 500, 'left', n.w - 28, 8.8);
          }
        }
        if (on('version') && o.release && room()) {
          const [x0, yy] = P(14, row());
          const behind = o.behind > 0;
          let w = chip(api, x0, yy + 3 * k, o.release + (behind ? ' · ' + o.behind + ' behind stable' : ''), behind ? 'warn' : 'muted');
          if (o.pendingInstall) chip(api, x0 + w + 4 * k, yy + 3 * k, 'install pending', 'warn');
        }
        if (on('drift') && o.difference && room()) {
          const [x0, yy] = P(14, row());
          chip(api, x0, yy + 3 * k, o.difference.replace('-', ' '), o.difference === 'declared-only' ? 'muted' : 'warn');
        }
        if (n.kind === 'target' && on('backup')) { const [x0, yy] = P(14, 60); api.text('Backup reports not-configured', x0, yy, 8.4, t.bad, 650, 'left', n.w - 28, 8.8); }
      }
      if (open > 0.02) {
        // Level 4: inside the service.
        ctx.globalAlpha = base * open;
        const ins = state.facts.inside[n.id] || {};
        let y = 60;
        const fs = 6.6, lh = 9.4;
        const line = (str, color, weight, glyph) => {
          if (y > n.h - 6) return;
          const [x0, yy] = P(14, y);
          if (glyph) glyph(x0 + 3 * k, yy - 2.2 * k);
          api.text(str, x0 + (glyph ? 9 * k : 0), yy, fs, color, weight || 400, 'left', n.w - 34);
          y += lh;
        };
        const slots = o.slots || ins.slots;
        if (slots) line('Slots ' + slots[0] + ' of ' + slots[1] + ' busy', t.ink, 600);
        for (const it of ins.items || []) line(it.text, t.ink, 400, (x, yy) => itemDot(ctx, x, yy, it.kind, k));
        for (const l of idx.locs.get(n.id) || []) {
          const s = locStatus(l);
          line(l.label + ' · ' + gb(l.sizeGb) + ' · ' + STATUS_WORD[s], s === 'gap' ? t.bad : s === 'local' ? t.warn : t.muted, s === 'gap' || s === 'local' ? 600 : 400, (x, yy) => shield(ctx, x, yy, Math.max(2.5, 3.6 * k), s));
        }
        for (const w of ws) line(w.title, w.severity === 'bad' ? t.bad : t.warn, 600, (x, yy) => cone(ctx, x, yy, Math.max(2.5, 3.4 * k), w.severity === 'bad' ? 'bad' : 'warn', 1));
      }
      ctx.globalAlpha = base;
    }
    // Work items wear the work-kind colour (coding, gate, review); facts stay neutral.
    function itemDot(ctx, x, y, kind, k) {
      ctx.beginPath(); ctx.arc(x, y, Math.max(1.5, 2.4 * k), 0, Math.PI * 2);
      ctx.fillStyle = kind === 'info' ? state.T.faint : state.T[kind] || state.T.link; ctx.fill();
    }

    function linkStyle(bl, maxTraffic) {
      const t = T();
      const l0 = bl.links[0];
      if (bl.links.every(l => l.layer === 'backup')) {
        if (!on('backup')) return { alpha: 0 };
        if (l0.kind === 'copy') return { color: t.good, dash: [2, 4], width: 1.8 };
        return { color: t.bad, dash: [6, 4], width: 1.8 };
      }
      const real = bl.links.filter(l => !l.layer);
      if (!real.length) return { alpha: 0 };
      const kind = real.every(l => l.kind === real[0].kind) ? real[0].kind : 'mixed';
      const dash = { private: [], tunnel: [10, 5], forwarder: [3, 3], cdn: [1.5, 3.5], https: [7, 3, 1.5, 3], mixed: [] }[kind] || [];
      const out = { color: t.muted, width: 1.1, dash, alpha: 0.85 };
      if (on('perf')) {
        const traffic = real.reduce((s, l) => s + (l.traffic || 0), 0);
        out.width = 1 + 2.6 * Math.sqrt(traffic / maxTraffic);
        out.flow = traffic > 0 && state.motion !== 'off';
        out.flowColor = t.muted;
        out.alpha = 1;
      }
      if (state.spec && (state.spec.annotations || []).some(a => a.link && real.some(l => l.id === a.link))) { out.color = t.link; out.width = Math.max(out.width, 2); }
      return out;
    }
    // Recompute traffic and state of bundles from the links they carry.
    function linkLabel(api, bl, c, alpha) {
      const t = T(), k = api.st.k;
      const real = bl.links.filter(l => !l.layer);
      const show = bl.down || (on('perf') && k > 0.42 && real.length && (real.some(l => l.kind === 'tunnel' || l.kind === 'cdn') || k > 1.05)) || (on('backup') && real.length === 0 && k > 0.42);
      if (!show) return;
      const mid = api.curvePoint(c, 0.5);
      let str;
      if (real.length === 0) str = bl.links[0].kind === 'copy' ? 'git mirror · last push 2 min ago' : 'backup job missing';
      else if (bl.down) str = (real.length === 1 ? real[0].label : real.length + ' links') + ' down';
      else {
        const lat = Math.max(...real.map(l => l.latencyMs || 0)), tr = real.reduce((s, l) => s + (l.traffic || 0), 0);
        str = (real.length === 1 ? real[0].label : real.length + ' links') + ' · ' + lat + ' ms · ' + fmtBytes(tr);
      }
      const ctx = api.ctx;
      ctx.save(); ctx.globalAlpha = alpha;
      ctx.font = '600 10.5px ' + (t.font || 'system-ui');
      const w = ctx.measureText(str).width + 12;
      ctx.fillStyle = bl.down ? t.badSoft : t.paper; ctx.strokeStyle = bl.down ? t.bad : t.line; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.roundRect ? ctx.roundRect(mid[0] - w / 2, mid[1] - 9, w, 18, 9) : ctx.rect(mid[0] - w / 2, mid[1] - 9, w, 18); ctx.fill(); ctx.stroke();
      ctx.fillStyle = bl.down ? t.bad : t.muted; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillText(str, mid[0], mid[1] + 0.5);
      ctx.restore();
    }

    // Persona emphasis: a soft halo outside the node, distinct from selection.
    function overNodes(api, nodes) {
      if (!state.spec || !state.spec.emphasis) return;
      const ctx = api.ctx, t = T();
      for (const e of state.spec.emphasis) {
        const n = state.scene.byId.get(e.node);
        if (!n) continue;
        const r = api.rep(n);
        if (r !== n || !r.q || r.alpha < 0.3 || !nodes.includes(r)) continue;
        ctx.save();
        ctx.strokeStyle = t.link; ctx.globalAlpha = e.weight === 'strong' ? 0.2 : 0.13; ctx.lineWidth = e.weight === 'strong' ? 9 : 7;
        api.pathQuad(api.grow(r.q, 6), 16 * api.st.k); ctx.stroke();
        ctx.restore();
      }
    }

    // Assessment callouts. Far out they are numbered markers listed in the side
    // panel; closer in they open as bubbles of fixed screen size.
    function anchors(api) {
      const out = [];
      (state.spec.annotations || []).forEach((a, i) => {
        let ax, ay;
        if (a.link) {
          const bl = (api.st.bundles || []).find(b => b.links.some(l => l.id === a.link));
          if (!bl || !bl.curve) return;
          [ax, ay] = api.curvePoint(bl.curve, 0.35);
        } else {
          const n = state.scene.byId.get(a.node);
          if (!n) return;
          const r = api.rep(n);
          if (!r.q || r.alpha < 0.3) return;
          ax = r.q[1][0] - 8 - (out.filter(o => o.node === r.id).length * 18); ay = r.q[1][1] + 8;
          out.push({ a, i, ax, ay, node: r.id });
          return;
        }
        out.push({ a, i, ax, ay, node: null });
      });
      return out.filter(o => o.ax > -20 && o.ax < api.st.w + 20 && o.ay > -20 && o.ay < api.st.h + 20);
    }
    function overlay(api) {
      if (!state.spec || !state.spec.annotations) return;
      const ctx = api.ctx, t = T();
      const list = anchors(api);
      const bubbles = api.st.k >= 0.75;
      const placed = [];
      for (const { a, i, ax, ay } of list) {
        ctx.save();
        if (!bubbles) {
          // Numbered marker.
          ctx.fillStyle = tone(a.tone); ctx.strokeStyle = t.paper; ctx.lineWidth = 2;
          ctx.beginPath(); ctx.arc(ax, ay - 14, 9, 0, Math.PI * 2); ctx.fill(); ctx.stroke();
          ctx.fillStyle = t.paper; ctx.font = '700 11px ' + (t.font || 'system-ui'); ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
          ctx.fillText(String(i + 1), ax, ay - 13.5);
          ctx.restore();
          continue;
        }
        ctx.font = '650 12px ' + (t.font || 'system-ui');
        // Title only; the full text opens for the selected node.
        const full = state.selectedId && (a.node === state.selectedId || (a.link && state.scene.links.some(l => l.id === a.link && (l.from === state.selectedId || l.to === state.selectedId))));
        const w = full ? Math.min(240, Math.max(ctx.measureText(a.title).width + 48, 170)) : ctx.measureText(a.title).width + 40;
        const lines = full ? wrap(ctx, a.text || '', w - 22, '400 11px ' + (t.font || 'system-ui'), 3) : [];
        const h = full ? 26 + lines.length * 14 : 26;
        let bx = Math.min(api.st.w - w - 6, Math.max(6, ax + 18)), by = Math.max(6, ay - h - 16);
        for (let j = 0; j < 10 && placed.some(p => bx < p.x + p.w + 6 && bx + w + 6 > p.x && by < p.y + p.h + 6 && by + h + 6 > p.y); j++) by += 22;
        placed.push({ x: bx, y: by, w, h });
        ctx.strokeStyle = tone(a.tone); ctx.lineWidth = 1.2;
        ctx.beginPath(); ctx.moveTo(ax, ay); ctx.lineTo(bx + 14, by + h); ctx.stroke();
        dot(ctx, ax, ay, 3.5, a.tone, false);
        ctx.fillStyle = t.paper; ctx.strokeStyle = t.line;
        ctx.shadowColor = 'rgba(0,0,0,0.12)'; ctx.shadowBlur = 8; ctx.shadowOffsetY = 2;
        ctx.beginPath(); ctx.roundRect ? ctx.roundRect(bx, by, w, h, 8) : ctx.rect(bx, by, w, h); ctx.fill();
        ctx.shadowColor = 'transparent'; ctx.stroke();
        ctx.fillStyle = tone(a.tone); ctx.beginPath(); ctx.arc(bx + 14, by + 14, 8, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = t.paper; ctx.font = '700 10px ' + (t.font || 'system-ui'); ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.fillText(String(i + 1), bx + 14, by + 14.5);
        ctx.fillStyle = t.ink; ctx.textAlign = 'left'; ctx.font = '650 12px ' + (t.font || 'system-ui');
        ctx.fillText(a.title, bx + 28, by + 14.5);
        ctx.font = '400 11px ' + (t.font || 'system-ui'); ctx.fillStyle = t.muted;
        lines.forEach((ln, j) => ctx.fillText(ln, bx + 11, by + 32 + j * 14));
        ctx.restore();
      }
    }
    function wrap(ctx, str, width, font, max) {
      ctx.font = font;
      const words = str.split(/\s+/), out = [];
      let cur = '';
      for (const w of words) {
        const next = cur ? cur + ' ' + w : w;
        if (ctx.measureText(next).width > width && cur) { out.push(cur); cur = w; if (out.length === max) break; } else cur = next;
      }
      if (out.length < max && cur) out.push(cur);
      if (out.length === max && words.join(' ').length > out.join(' ').length) out[max - 1] = out[max - 1].replace(/\s*\S*$/, '') + '…';
      return out;
    }

    function afterFrame(st) {
      if (animating) { animating = false; state.requestRedraw(); }
      lastEasing = [...easing]; easing.clear();
    }
    let lastEasing = [];

    return {
      hooks: { nodeStyle, decorate, linkStyle, linkLabel, overlay, overNodes, height, afterFrame },
      reindex, easingKeys: () => lastEasing, backupOf: (n, r) => backupOf(n, r), warningsOf: (n, r) => warningsOf(n, r), locStatus,
    };
  }

  const api = { LAYERS, createPainter, locStatus, pressure, daysToFull, STATUS_WORD, STATUS_TONE, fmtBytes, gb };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.LSCLayers = api;
})(typeof window !== 'undefined' ? window : globalThis);
