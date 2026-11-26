/* Simulated live feed. One tick per second. The numbers are causally linked:
   tasks arrive -> coding slots -> review slots -> gate batch -> verdict through the tunnel.
   CPU, memory, disk and link rates follow from that state plus a little noise. */
(function () {
  'use strict';
  const SV = window.SV;
  const HIST = 90;
  const FIX_AT = 75; // seconds after load at which review-3 gets its dependency

  function rng(seed) { let a = seed >>> 0; return function () { a = (a + 0x6d2b79f5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }

  const S = (SV.sim = {
    t: -150, tickAt: 0, paused: false, m: {}, link: {}, hist: {}, queues: { coding: 0, review: 0, gate: 0 },
    events: [], pips: [], warnings: [], listeners: [], fixAt: FIX_AT,
  });
  let rand = rng(20261118);
  let jobSeq = 4100;
  const rr = (a, b) => a + (b - a) * rand();
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const RUN = ['coding', 'reviewr', 'r1', 'r2', 'r3'];
  const SUITES = ['build', 'unit', 'contract', 'end to end', 'visual'];

  function init() {
    rand = rng(20261118); jobSeq = 4100;
    S.t = -150; S.fixAt = FIX_AT; S.events = []; S.pips = []; S.hist = {}; S.queues = { coding: 2, review: 1, gate: 0 };
    S.m = {}; S.link = {};
    SV.order.forEach((n) => {
      const m = (S.m[n.id] = { cpu: 0, mem: 0 });
      if (n.slots) { m.slots = []; for (let i = 0; i < n.slots; i++) m.slots.push(null); m.slotCh = new Array(n.slots).fill(-1e9); m.held = 0; }
      if (n.cores) { m.cores = new Array(n.cores).fill(0.1); m.coreW = m.cores.map(() => rr(0.5, 1.5)); }
    });
    S.m.r3.held = 2; S.m.r3.down = true;
    S.m.gate.job = null;
    SV.links.forEach((l) => (S.link[l.id] = { rate: 0 }));
    S.link.pub.rate = 18; S.pubWalk = 18; S.cache = 0.45; S.overlayAct = 0; S.latency = 24;
    S.warnings = [
      { id: 'backup', node: 'api', sev: 'warn', title: 'Backup not configured', short: 'backup not configured', tiny: 'no backup', text: 'The backup feature is present but has no encryption key and no off-host target. The only copy is one manual snapshot on the same disk array as the data.', action: 'Set a key and a target outside the app host, then run a first backup.', sinceMin: 6 * 24 * 60, state: 'open' },
      { id: 'disk', node: 'appdisk', sev: 'warn', title: 'Disk 84% full', short: 'disk 84% full', tiny: '84% full', text: 'The app host disk array is above the 80% alert threshold. The workspace repository and the manual snapshot are the largest items.', action: 'Prune old worktrees or move the snapshot off the host.', sinceMin: 3 * 60 + 12, state: 'open' },
      { id: 'dep', node: 'r3', sev: 'warn', title: 'Missing dependency', short: 'missing dependency', tiny: 'missing dep.', text: 'review-3 started without the verification toolchain in its image. Both slots are held back, so the shared review queue drains more slowly.', action: 'Rebuild the image with the dependency and restart the container.', sinceMin: 14, state: 'open' },
    ];
    for (let i = 0; i < 150; i++) step(true);
    S.events = S.events.slice(-4);
  }

  function push(id, v) { const h = S.hist[id] || (S.hist[id] = []); h.push(v); if (h.length > HIST) h.shift(); }
  function event(text, node, sev) { S.events.push({ t: S.t, wall: Date.now(), text, node, sev: sev || 'info' }); if (S.events.length > 40) S.events.shift(); }
  function pip(link, dir, quiet) { if (!quiet) S.pips.push({ link, dir, at: performance.now() }); S.link[link].ev = (S.link[link].ev || 0) + 1; }
  function used(id) { let c = 0; const s = S.m[id].slots; for (let i = 0; i < s.length; i++) if (s[i]) c++; return c; }
  S.used = used;
  S.cap = (id) => SV.nodes[id].slots - (S.m[id].held || 0);

  function start(id, kind, quiet) {
    const m = S.m[id], n = SV.nodes[id];
    const free = [];
    for (let i = 0; i < n.slots - m.held; i++) if (!m.slots[i]) free.push(i);
    if (!free.length) return false;
    const i = free[0];
    m.slots[i] = { id: jobSeq++, start: S.t, dur: kind === 'coding' ? Math.round(rr(20, 50)) : Math.round(rr(32, 68)) };
    m.slotCh[i] = quiet ? -1e9 : performance.now();
    if (id === 'coding') pip('apicoding', 1, quiet); else if (id === 'reviewr') pip('apireview', 1, quiet);
    return true;
  }

  function step(quiet) {
    S.t += 1;
    S.tickAt = performance.now();
    const q = S.queues, t = S.t;

    // review-3 gets fixed
    if (t === S.fixAt && S.m.r3.down) {
      S.m.r3.down = false; S.m.r3.restart = 4;
      event('review-3: dependency installed, container restarting', 'r3', 'info');
    }
    if (S.m.r3.restart) {
      S.m.r3.restart -= 1;
      if (!S.m.r3.restart) {
        S.m.r3.held = 0; S.m.r3.slotCh = S.m.r3.slotCh.map(() => (quiet ? -1e9 : performance.now()));
        const w = S.warnings.find((x) => x.id === 'dep'); w.state = 'resolved'; w.resolvedAt = t; w.resolvedWall = Date.now();
        event('review-3: back in service, 2 slots available', 'r3', 'ok');
      }
    }

    // arrivals (with backpressure so the sample never runs away)
    const lam = 0.2 + 0.085 * Math.sin(t / 47) + 0.03 * Math.sin(t / 11);
    if (q.coding < 12 && rand() < lam) q.coding += 1;

    // finish
    RUN.forEach((id) => {
      const m = S.m[id];
      m.slots.forEach((s, i) => {
        if (s && t >= s.start + s.dur) {
          m.slots[i] = null; m.slotCh[i] = quiet ? -1e9 : performance.now();
          if (id === 'coding') { q.review += 1; pip('apicoding', -1, quiet); }
          else { q.gate += 1; if (id === 'reviewr') pip('apireview', -1, quiet); }
        }
      });
    });
    const g = S.m.gate;
    if (g.job && t >= g.job.start + g.job.dur) {
      const failed = rand() < 0.16 ? 1 : 0;
      if (failed) q.coding += 1;
      event('gate: batch of ' + g.job.batch + ' verified' + (failed ? ', 1 sent back to coding' : ', all passed'), 'gate', failed ? 'warn' : 'ok');
      pip('tunnel', 1, quiet);
      g.job = null; g.ch = quiet ? -1e9 : performance.now();
    }

    // dispatch
    while (q.coding > 0 && start('coding', 'coding', quiet)) q.coding -= 1;
    while (q.review > 0) {
      let best = null, bestFree = 0;
      ['reviewr', 'r1', 'r2', 'r3'].forEach((id) => { const f = S.cap(id) - used(id); if (f > bestFree) { best = id; bestFree = f; } });
      if (!best) break;
      start(best, 'review', quiet); q.review -= 1;
    }
    if (!g.job && q.gate > 0 && (q.gate >= 2 || rand() < 0.5)) {
      const batch = Math.min(4, q.gate); q.gate -= batch;
      g.job = { id: jobSeq++, batch, start: t, dur: Math.round(rr(11, 19)) };
      g.ch = quiet ? -1e9 : performance.now();
      pip('tunnel', -1, quiet);
    }

    // link rates
    S.pubWalk = clamp(S.pubWalk + rr(-3.2, 3.2) + (rand() < 0.06 ? rr(6, 16) : 0) - (S.pubWalk - 17) * 0.06, 2, 58);
    S.cache = clamp(S.cache + rr(-0.03, 0.03), 0.3, 0.62);
    const L = S.link;
    L.pub.rate = S.pubWalk;
    L.origin.rate = S.pubWalk * (1 - S.cache);
    L.webapi.rate = L.origin.rate * 0.72;
    S.overlayAct = rand() < 0.07 ? rr(40, 240) : S.overlayAct * 0.55;
    L.overlay.rate = S.overlayAct < 1 ? 0.3 : S.overlayAct;
    S.latency = clamp(S.latency + rr(-2.5, 2.5), 14, 46);
    L.tunnel.rate = 0.4 + (g.job ? rr(3, 9) : 0) + (L.tunnel.ev ? 40 : 0);
    L.apicoding.rate = (L.apicoding.ev || 0) * 60 * 0.25 + (L.apicoding.rate || 0) * 0.75;
    L.apireview.rate = (L.apireview.ev || 0) * 60 * 0.25 + (L.apireview.rate || 0) * 0.75;
    L.tunnel.ev = 0; L.apicoding.ev = 0; L.apireview.ev = 0;

    // per-unit cpu (cores) and memory (GB)
    const M = S.m;
    const uc = used('coding'), ur = used('reviewr');
    M.coding.cores_ = uc * rr(0.36, 0.5) + 0.05; M.coding.mem = 1.1 + uc * 0.62 + rr(-0.1, 0.1);
    M.reviewr.cores_ = ur * rr(0.26, 0.36) + 0.04; M.reviewr.mem = 0.7 + ur * 0.48 + rr(-0.05, 0.05);
    M.api.cores_ = 0.25 + L.webapi.rate * 0.016 + rr(0, 0.1); M.api.mem = clamp((M.api.mem || 2.1) + rr(-0.03, 0.035), 1.8, 2.9);
    M.web.cores_ = 0.06 + L.origin.rate * 0.007 + rr(0, 0.03); M.web.mem = clamp((M.web.mem || 0.42) + rr(-0.01, 0.01), 0.36, 0.5);
    const appCores = M.coding.cores_ + M.reviewr.cores_ + M.api.cores_ + M.web.cores_ + 0.3;
    ['r1', 'r2', 'r3'].forEach((id) => { const u = used(id); M[id].cores_ = M[id].down || M[id].restart ? 0.01 : u * rr(0.45, 0.62) + 0.03; M[id].mem = M[id].down ? 0.2 : 0.6 + u * 1.9 + rr(-0.08, 0.08); });
    M.gate.cores_ = g.job ? rr(2.6, 3.9) : 0.06; M.gate.mem = g.job ? 5.2 + rr(-0.3, 0.6) : 1.3 + rr(-0.03, 0.03);
    const vmCores = M.r1.cores_ + M.r2.cores_ + M.r3.cores_ + M.gate.cores_ + 0.22;
    function host(id, cores, memSum, memBase) {
      const n = SV.nodes[id], m = M[id];
      m.cpu = clamp(cores / n.cores, 0, 1);
      m.mem = memSum + memBase; m.memFrac = m.mem / n.memMax;
      m.coreW = m.coreW.map((w) => clamp(w + rr(-0.22, 0.22), 0.35, 1.7));
      const sw = m.coreW.reduce((a, b) => a + b, 0);
      m.cores = m.coreW.map((w) => clamp((cores * w) / sw, 0.02, 1));
    }
    host('app', appCores, M.coding.mem + M.reviewr.mem + M.api.mem + M.web.mem, 3.4);
    host('vm', vmCores, M.r1.mem + M.r2.mem + M.r3.mem + M.gate.mem, 2.2);
    ['coding', 'reviewr', 'api', 'web'].forEach((id) => { M[id].cpu = M[id].cores_ / 12; M[id].memFrac = M[id].mem / 64; });
    ['r1', 'r2', 'r3', 'gate'].forEach((id) => { M[id].cpu = M[id].cores_ / 12; M[id].memFrac = M[id].mem / 50; });
    M.ws.cpu = clamp(M.vm.cpu * 0.82 + 0.05 + rr(-0.01, 0.01), 0, 1);
    M.stack.cpu = M.api.cpu + M.web.cpu; M.stack.mem = M.api.mem + M.web.mem;

    // storage
    const rwSeg = SV.nodes.vmdisk.segs[1];
    const rv = used('r1') + used('r2') + used('r3');
    rwSeg.target = 26 + rv * 4.1 + (g.job ? 6 : 0);
    rwSeg.gb += (rwSeg.target - rwSeg.gb) * 0.25;
    const repo = SV.nodes.appdisk.segs[2];
    repo.gb = 163 + (uc + ur) * 0.06 + Math.sin(t / 60) * 0.1;
    ['appdisk', 'vmdisk'].forEach((id) => { const n = SV.nodes[id]; const u = n.segs.reduce((a, s) => a + s.gb, 0); M[id].used = u; M[id].frac = u / n.capacity; });

    // history
    SV.order.forEach((n) => {
      const m = M[n.id];
      if (n.unit || n.perfHost) { push(n.id + '.cpu', m.cpu); push(n.id + '.mem', m.mem); }
      if (n.slots) push(n.id + '.slots', used(n.id));
      if (n.gate) push(n.id + '.slots', g.job ? 1 : 0);
      if (n.kind === 'disk') push(n.id + '.used', m.used);
    });
    push('q.coding', q.coding); push('q.review', q.review); push('q.gate', q.gate);
    push('q.total', q.coding + q.review + q.gate);
    SV.links.forEach((l) => push('l.' + l.id, L[l.id].rate));
    push('latency', S.latency);
    const busy = RUN.reduce((a, id) => a + used(id), 0);
    S.busy = busy; S.capTotal = RUN.reduce((a, id) => a + S.cap(id), 0);
    push('busy', busy);

    if (!quiet) S.listeners.forEach((f) => f());
  }

  S.suiteOf = function (job, tNow) {
    const p = clamp((tNow - job.start) / job.dur, 0, 0.999);
    const i = Math.floor(p * SUITES.length);
    return { name: SUITES[i], index: i + 1, total: SUITES.length, p };
  };
  S.now = function () { return S.paused ? S.t : S.t + clamp((performance.now() - S.tickAt) / 1000, 0, 1); };
  S.openWarnings = function () { return S.warnings.filter((w) => w.state === 'open'); };
  S.warningsFor = function (id) { return S.warnings.filter((w) => w.node === id); };
  S.onTick = function (f) { S.listeners.push(f); };
  S.replay = function () { const w = S.warnings.find((x) => x.id === 'dep'); w.state = 'open'; delete w.resolvedAt; w.sinceMin = 0; S.m.r3.down = true; S.m.r3.held = 2; S.m.r3.slots = [null, null]; S.fixAt = S.t + 45; event('review-3: restarted without its dependency (replay)', 'r3', 'warn'); S.listeners.forEach((f) => f()); };
  S.forward = function (n) { for (let i = 0; i < n; i++) step(true); };
  S.step = step;
  S.start = function () {
    init();
    setInterval(function () { if (!S.paused && !document.hidden) step(false); }, 1000);
  };
})();
