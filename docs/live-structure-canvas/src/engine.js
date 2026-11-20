// Live structure canvas engine for the AGT-3955 Dossier.
// One Canvas 2D surface: nested layout, camera with pan, zoom and pinch,
// semantic zoom by on-screen size, a 2D and a 2.5D isometric projection,
// link bundling to the nearest visible node, flow particles, event effects,
// hit testing and render on demand. Plain JavaScript without dependencies.
// Layers decorate through hooks; the engine knows no layer semantics.
(function (root) {
  'use strict';

  const C30 = Math.cos(Math.PI / 6);
  const S30 = 0.5;
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const smooth = (a, b, v) => { const t = clamp((v - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); };
  const lerp = (a, b, t) => a + (b - a) * t;
  const easeInOut = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2);
  const now = () => (typeof performance !== 'undefined' ? performance.now() : Date.now());

  // ---------- layout ----------
  // World units equal CSS pixels at zoom 1. Leaves have fixed sizes; groups
  // wrap their children in rows. Children with `row` start a new row.
  const LEAF = { container: [180, 116], external: [188, 76], target: [188, 76] };
  const BOX = {
    root: { pad: 0, head: 0, gap: 90, cols: 8 },
    site: { pad: 30, head: 54, gap: 34, cols: 3 },
    outside: { pad: 26, head: 50, gap: 22, cols: 6 },
    machine: { pad: 20, head: 46, gap: 18, cols: 3 },
    vm: { pad: 16, head: 38, gap: 16, cols: 3 },
  };
  // Opening thresholds in screen pixels of the node's width.
  const OPEN_AT = { site: 240, outside: 240, machine: 330, vm: 300, container: 330 };
  // Iso heights (world units) of each prism and its base offset above the parent top.
  const THICK = { site: 8, outside: 8, machine: 16, vm: 12, container: 18, external: 14, target: 10 };

  function buildScene(model) {
    const byId = new Map();
    const nodes = model.nodes.map((n, i) => ({ ...n, children: [], order: i, depth: 0 }));
    for (const n of nodes) byId.set(n.id, n);
    const rootNode = { id: '__root', kind: 'root', children: [], depth: -1, label: '' };
    for (const n of nodes) {
      const p = n.parent ? byId.get(n.parent) : rootNode;
      if (!p) throw new Error('Unknown parent ' + n.parent + ' of ' + n.id);
      n.parentNode = p === rootNode ? null : p;
      p.children.push(n);
    }
    const setDepth = (n, d) => { n.depth = d; for (const c of n.children) setDepth(c, d + 1); };
    for (const c of rootNode.children) setDepth(c, 0);
    measure(rootNode);
    place(rootNode, 0, 0, 0);
    const links = (model.links || []).map((l, i) => ({ ...l, order: i, phase: (i * 0.137) % 1 }));
    for (const l of links) {
      if (!byId.has(l.from) || !byId.has(l.to)) throw new Error('Link ' + l.id + ' names an unknown node');
      l.a = byId.get(l.from); l.b = byId.get(l.to);
    }
    return { root: rootNode, nodes, byId, links, bounds: { x: 0, y: 0, w: rootNode.w, h: rootNode.h } };
  }

  function isGroup(n) { return n.children && n.children.length > 0 && !LEAF[n.kind]; }

  function measure(n) {
    if (!isGroup(n)) {
      const s = LEAF[n.kind] || LEAF.container;
      n.w = n.wide ? s[0] * 2 + 18 : s[0]; n.h = s[1];
      return;
    }
    const b = BOX[n.kind] || BOX.machine;
    for (const c of n.children) measure(c);
    const rows = [];
    let row = [];
    for (const c of n.children) {
      if (row.length && (row.length >= (n.cols || b.cols) || c.row)) { rows.push(row); row = []; }
      row.push(c);
    }
    if (row.length) rows.push(row);
    let w = 0, h = 0;
    n.rows = rows.map(r => {
      const rw = r.reduce((s, c) => s + c.w, 0) + b.gap * (r.length - 1);
      const rh = Math.max(...r.map(c => c.h));
      w = Math.max(w, rw);
      return { items: r, w: rw, h: rh };
    });
    h = n.rows.reduce((s, r) => s + r.h, 0) + b.gap * (n.rows.length - 1);
    n.w = Math.max(w + b.pad * 2, n.minW || 0);
    n.h = h + b.pad * 2 + b.head;
  }

  function place(n, x, y, z) {
    n.x = x; n.y = y; n.z = z;
    n.th = THICK[n.kind] || 0;
    if (!n.rows) return;
    const b = BOX[n.kind] || BOX.machine;
    let cy = y + b.pad + b.head;
    for (const r of n.rows) {
      // Centre rows; align items to the row top.
      let cx = x + (n.w - r.w) / 2;
      for (const c of r.items) { place(c, cx, cy, z + n.th); cx += c.w + b.gap; }
      cy += r.h + b.gap;
    }
  }

  // ---------- projection ----------
  // m = 0 is plain 2D, m = 1 is the isometric ground plane with heights.
  function projector(m) {
    const a = 1 - m + m * C30, b = -m * C30, c = m * S30, d = 1 - m + m * S30;
    const det = a * d - b * c;
    return {
      m,
      p(x, y, z) { return [a * x + b * y, c * x + d * y - (z || 0) * m]; },
      inv(X, Y) { return [(d * X - b * Y) / det, (-c * X + a * Y) / det]; },
    };
  }

  // ---------- engine ----------
  function createEngine(canvas, opts) {
    opts = opts || {};
    let ctx = canvas.getContext('2d');
    const hooks = opts.hooks || {};
    const st = {
      scene: null, k: 1, tx: 0, ty: 0, w: 300, h: 200, dpr: 1,
      iso: 0, isoTarget: 0, isoFrom: 0, isoStart: 0,
      tween: null, effects: [], tokens: [], hidden: false, offscreen: false,
      raf: 0, timer: 0, last: 0, flow: opts.flow !== false, motion: opts.motion || 'lively',
      focusId: null, selectedId: null, hoverId: null, hits: [], frames: 0, drawMs: 0,
      fade: null, emphasis: null, tokensCss: {},
    };
    let proj = projector(0);

    function setScene(scene) { st.scene = scene; invalidate(); }
    function resize(w, h, dpr) {
      st.w = Math.max(50, w); st.h = Math.max(50, h); st.dpr = Math.min(dpr || 1, 2);
      canvas.width = Math.round(st.w * st.dpr); canvas.height = Math.round(st.h * st.dpr);
      canvas.style.width = st.w + 'px'; canvas.style.height = st.h + 'px';
      invalidate();
    }

    // Screen transform of a world point including the projection.
    function toScreen(x, y, z) { const q = proj.p(x, y, z); return [q[0] * st.k + st.tx, q[1] * st.k + st.ty]; }
    function toWorld(sx, sy) { return proj.inv((sx - st.tx) / st.k, (sy - st.ty) / st.k); }

    function projectedBounds(rect, z) {
      const pts = [[rect.x, rect.y], [rect.x + rect.w, rect.y], [rect.x, rect.y + rect.h], [rect.x + rect.w, rect.y + rect.h]].map(p => proj.p(p[0], p[1], z || 0));
      const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
      const minX = Math.min(...xs), minY = Math.min(...ys) - (proj.m ? 60 * proj.m : 0);
      return { x: minX, y: minY, w: Math.max(...xs) - minX, h: Math.max(...ys) - minY };
    }

    function cameraFor(rect, fill) {
      const pb = projectedBounds(rect, rect.z);
      const k = clamp(Math.min(st.w / pb.w, st.h / pb.h) * (fill || 0.9), 0.04, 12);
      return { k, tx: st.w / 2 - (pb.x + pb.w / 2) * k, ty: st.h / 2 - (pb.y + pb.h / 2) * k };
    }

    function animateTo(cam, ms) {
      if (st.motion === 'off' || ms === 0) { st.k = cam.k; st.tx = cam.tx; st.ty = cam.ty; st.tween = null; st.camEvent = true; invalidate(); return; }
      st.tween = { from: { k: st.k, tx: st.tx, ty: st.ty }, to: cam, start: now(), dur: ms || 520 };
      st.inMotion = true; request();
    }
    function fit(ms) { if (st.scene) animateTo(cameraFor(st.scene.bounds, 0.94), ms); }
    function focusRect(n, fill, ms) { animateTo(cameraFor(n, fill || 0.62), ms); }

    function zoomAt(sx, sy, factor) {
      st.tween = null;
      const k2 = clamp(st.k * factor, 0.04, 14);
      st.tx = sx - (sx - st.tx) * (k2 / st.k);
      st.ty = sy - (sy - st.ty) * (k2 / st.k);
      st.k = k2;
      moved();
    }
    function panBy(dx, dy) { st.tween = null; st.tx += dx; st.ty += dy; moved(); }

    function setIso(on) {
      const target = on ? 1 : 0;
      if (st.isoTarget === target) return;
      st.isoTarget = target; st.isoFrom = st.iso; st.isoStart = now();
      if (st.motion === 'off') { st.iso = target; proj = projector(target); }
      invalidate();
    }

    // ---------- semantic zoom ----------
    function openness(n) {
      const at = n.openAt || OPEN_AT[n.kind];
      if (!at) return 0;
      // Benchmark mode: every group open, no service interiors.
      if (opts.openAll) return n.kind === 'container' ? 0 : 1;
      // In 2.5D the blocks themselves carry the overview, so groups open earlier.
      const a2 = at * (1 - proj.m * 0.55);
      return smooth(a2, a2 * 1.32, n.w * st.k);
    }
    // Per frame: alpha of each node (its ancestors' openness) and its own openness.
    function computeVisibility() {
      const sc = st.scene;
      const vis = [];
      // A hidden subtree is zeroed, so no stale alpha survives from an earlier zoom.
      const hide = n => { n.alpha = 0; n.open = 0; n.q = null; for (const c of n.children) hide(c); };
      const walk = (n, alpha) => {
        n.open = isGroup(n) || n.kind === 'container' ? openness(n) : 0;
        n.alpha = alpha;
        if (alpha <= 0.01) { for (const c of n.children) hide(c); return; }
        vis.push(n);
        if (isGroup(n)) for (const c of n.children) walk(c, alpha * n.open);
      };
      for (const c of sc.root.children) walk(c, 1);
      return vis;
    }
    // Representative of a node: the deepest ancestor-or-self drawn as a whole.
    function rep(n) {
      const chain = [];
      for (let p = n; p; p = p.parentNode) chain.unshift(p);
      for (const p of chain) if (isGroup(p) && p.open < 0.5) return p;
      return n;
    }
    function level() {
      // 1: no machine visible; 2: machines but no container; 3: containers; 4: a container opened.
      const sc = st.scene;
      if (!sc) return 1;
      let lv = 1;
      const in_ = n => n.q && n.q[2][0] > 0 && n.q[0][0] < st.w && n.q[2][1] > 0 && n.q[0][1] < st.h;
      for (const n of sc.nodes) {
        if (n.alpha === undefined || n.alpha < 0.5 || !in_(n)) continue;
        if (n.kind === 'machine' || n.kind === 'vm') lv = Math.max(lv, 2);
        if (n.kind === 'container') lv = Math.max(lv, n.open >= 0.5 ? 4 : 3);
      }
      return lv;
    }

    // ---------- links ----------
    function bundleLinks() {
      const map = new Map();
      for (const l of st.scene.links) {
        if (l.hidden) continue;
        const a = rep(l.a), b = rep(l.b);
        if (a === b) continue;
        if (a.alpha < 0.05 || b.alpha < 0.05) continue;
        const key = a.id < b.id ? a.id + '|' + b.id : b.id + '|' + a.id;
        let bl = map.get(key);
        if (!bl) { bl = { key, a, b, links: [], traffic: 0, down: false, kind: l.kind, latency: 0, phase: l.phase }; map.set(key, bl); }
        bl.links.push(l);
        bl.traffic += l.traffic || 0;
        if (l.state === 'down') bl.down = true;
        if (l.kind !== bl.kind) bl.kind = 'mixed';
        bl.latency = Math.max(bl.latency, l.latencyMs || 0);
      }
      return [...map.values()];
    }
    function centerOf(n) { return [n.x + n.w / 2, n.y + n.h / 2, n.z + (n.hz == null ? n.th : n.hz)]; }
    function clipToRect(n, px, py, qx, qy) {
      // Point where the segment from the centre p to q leaves the rect of n.
      const hw = n.w / 2, hh = n.h / 2, dx = qx - px, dy = qy - py;
      const tx = dx ? hw / Math.abs(dx) : Infinity, ty = dy ? hh / Math.abs(dy) : Infinity;
      const t = Math.min(tx, ty, 1);
      return [px + dx * t, py + dy * t];
    }
    // Curve in screen space: start, control, end.
    function linkCurve(bl) {
      const [ax, ay, az] = centerOf(bl.a), [bx, by, bz] = centerOf(bl.b);
      let s = [ax, ay], e = [bx, by];
      if (proj.m < 0.5) { s = clipToRect(bl.a, ax, ay, bx, by); e = clipToRect(bl.b, bx, by, ax, ay); }
      const S = toScreen(s[0], s[1], az), E = toScreen(e[0], e[1], bz);
      const mx = (S[0] + E[0]) / 2, my = (S[1] + E[1]) / 2;
      const dx = E[0] - S[0], dy = E[1] - S[1], len = Math.hypot(dx, dy) || 1;
      const bend = Math.min(60, len * 0.12) * (bl.key.length % 2 ? 1 : -1);
      return { S, E, C: [mx - (dy / len) * bend, my + (dx / len) * bend], len };
    }
    function curvePoint(c, t) {
      const u = 1 - t;
      return [u * u * c.S[0] + 2 * u * t * c.C[0] + t * t * c.E[0], u * u * c.S[1] + 2 * u * t * c.C[1] + t * t * c.E[1]];
    }

    // ---------- tokens and effects ----------
    // A token travels along a chain of node ids; each hop follows the bundle curve.
    function addToken(tok) {
      if (st.motion === 'off') return;
      st.tokens.push({ ...tok, start: now(), hop: 0, t: 0 });
      request();
    }
    function addEffect(fx) {
      if (st.motion === 'off') return;
      if (fx.id) st.effects = st.effects.filter(e => e.id !== fx.id);
      st.effects.push({ ...fx, start: now() });
      request();
    }
    function removeEffect(id) { st.effects = st.effects.filter(e => e.id !== id); request(); }

    // ---------- drawing primitives ----------
    function quad(x, y, w, h, z) {
      return [toScreen(x, y, z), toScreen(x + w, y, z), toScreen(x + w, y + h, z), toScreen(x, y + h, z)];
    }
    function pathQuad(q, r) {
      ctx.beginPath();
      if (proj.m < 0.02 && r) {
        const x = q[0][0], y = q[0][1], w = q[1][0] - x, h = q[3][1] - y;
        const rr = Math.min(r, w / 2, h / 2);
        ctx.moveTo(x + rr, y); ctx.arcTo(x + w, y, x + w, y + h, rr); ctx.arcTo(x + w, y + h, x, y + h, rr);
        ctx.arcTo(x, y + h, x, y, rr); ctx.arcTo(x, y, x + w, y, rr); ctx.closePath();
        return;
      }
      ctx.moveTo(q[0][0], q[0][1]); for (let i = 1; i < 4; i++) ctx.lineTo(q[i][0], q[i][1]); ctx.closePath();
    }
    function prismSides(n, fillA, fillB) {
      const th = n.hz == null ? n.th : n.hz;
      if (proj.m < 0.02 || !th) return;
      const z0 = n.z, z1 = n.z + th;
      const left = [toScreen(n.x, n.y + n.h, z0), toScreen(n.x + n.w, n.y + n.h, z0), toScreen(n.x + n.w, n.y + n.h, z1), toScreen(n.x, n.y + n.h, z1)];
      const right = [toScreen(n.x + n.w, n.y, z0), toScreen(n.x + n.w, n.y + n.h, z0), toScreen(n.x + n.w, n.y + n.h, z1), toScreen(n.x + n.w, n.y, z1)];
      ctx.fillStyle = fillA; pathQuad(left); ctx.fill();
      ctx.fillStyle = fillB; pathQuad(right); ctx.fill();
    }
    // Label widths are measured once at 100 px and scaled; truncation is cached.
    const widthCache = new Map();
    const fitCache = new Map();
    function widthAt(str, weight, px) {
      const key = weight + '|' + str;
      let w = widthCache.get(key);
      if (w === undefined) {
        ctx.font = weight + ' 100px ' + (st.tokensCss.font || 'system-ui, sans-serif');
        w = ctx.measureText(str).width; widthCache.set(key, w);
      }
      return w * px / 100;
    }
    function fitText(str, weight, availPx, px) {
      // Truncation depends only on the ratio of available width to font size.
      const key = weight + '|' + str + '|' + Math.round(availPx / px * 20);
      let s2 = fitCache.get(key);
      if (s2 !== undefined) return s2;
      s2 = str;
      if (widthAt(s2, weight, px) > availPx) {
        while (s2.length > 2 && widthAt(s2 + '…', weight, px) > availPx) s2 = s2.slice(0, -1);
        s2 += '…';
      }
      fitCache.set(key, s2);
      return s2;
    }
    // Text in screen space. size is in world units; minPx keeps labels readable
    // when the camera is far out, as long as the available width allows it.
    function text(str, x, y, size, color, weight, align, maxW, minPx) {
      if (!str) return 0;
      let px = size * st.k;
      weight = weight || 400;
      if (minPx && px < minPx) px = minPx;
      if (px < 3.2) return 0;
      const avail = maxW ? maxW * st.k : Infinity;
      if (avail < px * 2.2) return 0;
      const s2 = maxW ? fitText(String(str), weight, avail, px) : String(str);
      const w = widthAt(s2, weight, px);
      const gx = align === 'center' ? x - w / 2 : align === 'right' ? x - w : x;
      if (px < 6) {
        // Greeking: a soft bar instead of unreadable glyphs.
        ctx.fillStyle = color; const a = ctx.globalAlpha; ctx.globalAlpha = a * 0.28;
        ctx.fillRect(gx, y - px * 0.8, w, px * 0.7);
        ctx.globalAlpha = a;
        return w;
      }
      ctx.font = weight + ' ' + px.toFixed(1) + 'px ' + (st.tokensCss.font || 'system-ui, sans-serif');
      ctx.fillStyle = color; ctx.textAlign = 'left'; ctx.textBaseline = 'alphabetic';
      ctx.fillText(s2, gx, y);
      return w;
    }
    // Screen offset for stacked text: the larger of the world offset and the font-based one.
    const lineY = (world, px) => Math.max(world * st.k, px);

    // ---------- frame ----------
    // Two layers. The static layer (frames, labels, links, layer decorations)
    // is drawn into an offscreen cache; the dynamic layer (flow particles,
    // tokens, effects, focus ring) is drawn over it every animated frame.
    // While the camera moves and a full draw is slow, the cache is blitted
    // with the camera delta and redrawn crisp once motion settles.
    const cacheCanvas = typeof document !== 'undefined' ? document.createElement('canvas') : null;
    const cacheCtx = cacheCanvas ? cacheCanvas.getContext('2d') : null;
    const mainCtx = ctx;
    const cache = { ok: false, k: 1, tx: 0, ty: 0, m: 0 };
    st.staticDirty = true; st.slowAvg = 0; st.inMotion = false; st.settle = 0; st.blits = 0; st.fullDraws = 0;

    function frame(t) {
      st.raf = 0; st.timer = 0;
      if (!st.scene || st.hidden) return;
      const t0 = now();
      const dt = st.last ? Math.min(0.1, (t - st.last) / 1000) : 0;
      st.last = t;
      let active = false;
      if (st.tween) {
        const p = clamp((t0 - st.tween.start) / st.tween.dur, 0, 1), e = easeInOut(p);
        st.k = Math.exp(lerp(Math.log(st.tween.from.k), Math.log(st.tween.to.k), e));
        st.tx = lerp(st.tween.from.tx, st.tween.to.tx, e); st.ty = lerp(st.tween.from.ty, st.tween.to.ty, e);
        if (p >= 1) { st.tween = null; settleSoon(); } else { active = true; st.inMotion = true; }
        st.camEvent = true;
      }
      if (st.iso !== st.isoTarget) {
        const p = clamp((t0 - st.isoStart) / 700, 0, 1);
        st.iso = lerp(st.isoFrom, st.isoTarget, easeInOut(p));
        if (p >= 1) st.iso = st.isoTarget; else active = true;
        proj = projector(st.iso);
        st.staticDirty = true;
        st.camEvent = true;
      }
      const camMoved = cache.k !== st.k || cache.tx !== st.tx || cache.ty !== st.ty;
      const ratio = st.k / cache.k;
      const shift = Math.max(Math.abs(st.tx - cache.tx * ratio), Math.abs(st.ty - cache.ty * ratio));
      const canBlit = cache.ok && !st.staticDirty && cache.m === proj.m &&
        (!camMoved || (st.inMotion && st.slowAvg > 12 && Math.abs(Math.log(ratio)) < 0.7 && shift < Math.min(st.w, st.h) * 0.5));
      if (!canBlit) {
        const s0 = now();
        ctx = cacheCtx || mainCtx;
        drawStatic();
        ctx = mainCtx;
        cache.ok = !!cacheCtx; cache.k = st.k; cache.tx = st.tx; cache.ty = st.ty; cache.m = proj.m;
        st.staticDirty = false; st.fullDraws++;
        st.slowAvg = st.slowAvg ? st.slowAvg * 0.7 + (now() - s0) * 0.3 : now() - s0;
      } else if (camMoved) st.blits++;
      composite(dt, t0);
      // Camera listeners read positions of this frame.
      if (st.camEvent || camMoved) { st.camEvent = false; emit('camera'); }
      if (st.effects.length || st.tokens.length) active = true;
      st.frames++; st.drawMs = now() - t0;
      if (hooks.afterFrame) hooks.afterFrame(st);
      if (active) { st.raf = requestAnimationFrame(frame); return; }
      // Only flow particles move: run them at about 30 frames per second.
      if (st.flowActive && st.motion === 'lively' && st.flow) {
        st.timer = setTimeout(() => { st.timer = 0; st.raf = requestAnimationFrame(frame); }, 33);
      } else st.last = 0;
    }
    function settleSoon() {
      clearTimeout(st.settle);
      st.settle = setTimeout(() => { st.inMotion = false; invalidate(); }, 140);
    }

    // Data, layer, selection or theme change: redraw the static layer.
    function invalidate() { st.staticDirty = true; request(); }
    // Camera change: the static layer may be reused while moving.
    function moved() { st.inMotion = true; settleSoon(); request(); }
    function request() {
      if (st.raf || st.hidden || st.offscreen) return;
      if (st.timer) { clearTimeout(st.timer); st.timer = 0; }
      st.raf = requestAnimationFrame(frame);
    }

    function composite(dt, t0) {
      const T = st.tokensCss;
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      if (cacheCanvas && cache.ok) {
        const s = st.k / cache.k, dx = st.tx - cache.tx * s, dy = st.ty - cache.ty * s;
        if (s !== 1 || dx || dy) { ctx.fillStyle = T.paper || '#fff'; ctx.fillRect(0, 0, canvas.width, canvas.height); }
        ctx.imageSmoothingEnabled = s !== 1;
        ctx.drawImage(cacheCanvas, dx * st.dpr, dy * st.dpr, cacheCanvas.width * s, cacheCanvas.height * s);
        // Dynamic marks live in the cache's screen space, moved by the same delta.
        ctx.setTransform(st.dpr * s, 0, 0, st.dpr * s, dx * st.dpr, dy * st.dpr);
      } else ctx.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
      drawFlow(dt);
      drawTokens(t0);
      drawEffects(t0);
      const f = st.focusId && st.scene.byId.get(st.focusId);
      if (f && f.q && f.alpha > 0.2) {
        const r = rep(f);
        const q = (r || f).q;
        if (q) {
          ctx.save(); ctx.globalAlpha = 1; ctx.strokeStyle = T.link || '#1f67b2'; ctx.lineWidth = 2.5; ctx.setLineDash([]);
          pathQuad(grow(q, 2), 11 * cache.k); ctx.stroke();
          ctx.restore();
        }
      }
      if (hooks.dynamic) hooks.dynamic(api, t0);
    }

    function drawStatic() {
      const T = st.tokensCss;
      if (cacheCanvas && (cacheCanvas.width !== canvas.width || cacheCanvas.height !== canvas.height)) { cacheCanvas.width = canvas.width; cacheCanvas.height = canvas.height; }
      ctx.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
      ctx.fillStyle = T.paper || '#fff';
      ctx.fillRect(0, 0, st.w, st.h);
      const vis = computeVisibility();
      st.hits = [];
      st.visible = vis;
      // Painter's order: parents first, siblings far to near in iso.
      const order = [];
      const walk = (n) => {
        if (n.alpha <= 0.01) return;
        order.push(n);
        if (isGroup(n) && n.open > 0.01) {
          const kids = proj.m > 0.02 ? [...n.children].sort((p, q) => (p.x + p.y + p.w / 2 + p.h / 2) - (q.x + q.y + q.w / 2 + q.h / 2)) : n.children;
          for (const c of kids) walk(c);
        }
      };
      const tops = proj.m > 0.02 ? [...st.scene.root.children].sort((p, q) => (p.x + p.y) - (q.x + q.y)) : st.scene.root.children;
      for (const c of tops) walk(c);
      const view = { x0: -40, y0: -40, x1: st.w + 40, y1: st.h + 40 };
      const onScreen = q => {
        let a = Infinity, b = Infinity, c = -Infinity, d = -Infinity;
        for (const p of q) { if (p[0] < a) a = p[0]; if (p[1] < b) b = p[1]; if (p[0] > c) c = p[0]; if (p[1] > d) d = p[1]; }
        return c >= view.x0 && a <= view.x1 && d >= view.y0 - 80 && b <= view.y1;
      };
      const isoMode = proj.m > 0.02;
      // 2D draws group frames, then links, then leaves; iso draws all prisms, then links on top.
      const leaves = [];
      for (const n of order) {
        n.hz = hooks.height ? hooks.height(n) : n.th;
        const top = quad(n.x, n.y, n.w, n.h, n.z + n.hz);
        n.q = top;
        if (!onScreen(top)) continue;
        if (!isoMode && !isGroup(n)) { leaves.push(n); continue; }
        drawNode(n, top);
      }
      const bundles = bundleLinks();
      st.bundles = bundles;
      drawLinks(bundles);
      for (const n of leaves) drawNode(n, n.q);
      // Link labels sit above the cards so a card never hides a latency or a broken link.
      if (hooks.linkLabel) for (const bl of st.labelled || []) { ctx.save(); hooks.linkLabel(api, bl, bl.curve, bl.alphaNow); ctx.restore(); }
      if (hooks.overNodes) hooks.overNodes(api, order.filter(n => n.q && onScreen(n.q)));
      if (hooks.overlay) hooks.overlay(api, order);
    }
    function grow(q, d) {
      const cx = (q[0][0] + q[2][0]) / 2, cy = (q[0][1] + q[2][1]) / 2;
      return q.map(p => { const dx = p[0] - cx, dy = p[1] - cy, l = Math.hypot(dx, dy) || 1; return [p[0] + dx / l * d, p[1] + dy / l * d]; });
    }

    function drawNode(n, top) {
      const T = st.tokensCss;
      const group = isGroup(n);
      const summaryAlpha = group ? 1 - n.open : 1;
      ctx.save();
      ctx.globalAlpha = n.alpha * (st.fade && st.fade(n) ? 0.35 : 1);
      const deco = hooks.nodeStyle ? hooks.nodeStyle(n) : {};
      const fill = deco.fill || (n.kind === 'site' || n.kind === 'outside' ? T.surfaceSoft : group ? T.surface : T.paper);
      prismSides(n, deco.sideA || T.sideA, deco.sideB || T.sideB);
      const r = (n.kind === 'site' || n.kind === 'outside' ? 16 : n.kind === 'external' || n.kind === 'target' ? 38 : 10) * st.k;
      ctx.fillStyle = fill; pathQuad(top, r); ctx.fill();
      ctx.lineWidth = deco.lineWidth || (group ? 1 : 1.2);
      ctx.strokeStyle = deco.stroke || T.line;
      ctx.setLineDash(deco.dash ? deco.dash.map(v => v * Math.max(0.5, Math.min(st.k, 2))) : []);
      ctx.stroke(); ctx.setLineDash([]);
      st.hits.push({ n, q: top });
      // Header: kind glyph, label and subtitle, anchored at the top-left corner of the top face.
      const b = BOX[n.kind] || { pad: 14 };
      const [hx, hy] = top[0];
      const pad = (group ? b.pad : 14) * st.k;
      const labelSize = n.kind === 'site' || n.kind === 'outside' ? 19 : group ? 15 : 13.5;
      const textX = hx + Math.max(pad, 8) + (proj.m > 0.02 ? 6 * st.k : 0);
      const maxW = n.w - (group ? b.pad : 14) * 2 - 36;
      const screenH = Math.abs(top[3][1] - top[0][1]);
      if (proj.m > 0.5) {
        // 2.5D: labels float above the block as billboards.
        if (group) {
          const [lx, ly] = toScreen(n.x, n.y + n.h, n.z + (n.hz == null ? n.th : n.hz));
          if (summaryAlpha > 0.5 || n.kind === 'site' || n.kind === 'outside') text(n.label, lx + 4, ly - 6, labelSize, T.ink, 650, 'left', n.w * 0.9, n.kind === 'site' || n.kind === 'outside' ? 12 : 10.5);
        } else {
          const [cx, cy] = toScreen(n.x + n.w / 2, n.y + n.h / 2, n.z + (n.hz == null ? n.th : n.hz));
          text(n.label, cx, cy - 4, 12, T.ink, 650, 'center', n.w * 1.3, 9.5);
        }
      } else if (n.kind === 'site' || n.kind === 'outside') {
        const ky = hy + lineY(20, 13);
        text((n.kicker || '').toUpperCase(), textX, ky, 9.5, T.faint, 700, 'left', maxW, 8.5);
        text(n.label, textX, ky + lineY(20, 16), labelSize, T.ink, 650, 'left', maxW, 12.5);
      } else {
        const lp = Math.max(labelSize * st.k, group ? 11.5 : 10.5);
        const ly = hy + lineY(group ? 30 : 26, lp + 4);
        text(n.label, textX, ly, labelSize, T.ink, 650, 'left', maxW, group ? 11.5 : 10.5);
        const sp = Math.max(10.5 * st.k, 9.5);
        if (n.sub && screenH > ly - hy + sp + 6) text(n.sub, textX, ly + lineY(16, sp + 3), 10.5, T.muted, 400, 'left', maxW, 9.5);
      }
      n.headPx = (n.kind === 'site' || n.kind === 'outside') ? lineY(40, 34) : lineY(group ? 46 : 42, 38);
      if (hooks.decorate) hooks.decorate(api, n, top, { summaryAlpha, group, x: hx, y: hy, s: st.k });
      ctx.restore();
    }

    function drawLinks(bundles) {
      const T = st.tokensCss;
      const maxTraffic = Math.max(1, ...bundles.map(b => b.traffic));
      const groups = new Map(), down = [], labelled = [];
      st.flowItems = [];
      for (const bl of bundles) {
        const style = hooks.linkStyle ? hooks.linkStyle(bl, maxTraffic) : {};
        const alpha = Math.min(bl.a.alpha, bl.b.alpha) * (style.alpha == null ? 1 : style.alpha);
        bl.curve = null;
        if (alpha < 0.03) continue;
        const c = linkCurve(bl);
        // Cull links whose control box is off screen.
        const x0 = Math.min(c.S[0], c.C[0], c.E[0]), x1 = Math.max(c.S[0], c.C[0], c.E[0]);
        const y0 = Math.min(c.S[1], c.C[1], c.E[1]), y1 = Math.max(c.S[1], c.C[1], c.E[1]);
        if (x1 < -20 || x0 > st.w + 20 || y1 < -20 || y0 > st.h + 20) continue;
        bl.curve = c; bl.alphaNow = alpha;
        if (bl.down) { down.push(bl); labelled.push(bl); continue; }
        const color = style.color || T.muted, width = style.width || 1.2, dash = style.dash || [];
        const key = color + '|' + width.toFixed(1) + '|' + dash.join(',') + '|' + alpha.toFixed(2);
        let g = groups.get(key);
        if (!g) { g = { color, width, dash, alpha, items: [] }; groups.set(key, g); }
        g.items.push(c);
        if (style.flow && st.motion !== 'off') {
          // Particle density follows traffic; speed is constant so motion stays calm.
          const count = Math.max(1, Math.round((c.len / 90) * (bl.traffic / maxTraffic) * 3));
          st.flowItems.push({ bl, c, count, color: style.flowColor || T.muted });
        }
        labelled.push(bl);
      }
      ctx.save();
      // One path per style: thousands of links cost a handful of strokes.
      for (const g of groups.values()) {
        ctx.globalAlpha = g.alpha; ctx.strokeStyle = g.color; ctx.lineWidth = g.width; ctx.setLineDash(g.dash);
        // One stroke per link: a shared path makes the rasteriser union every overlap (measured 2x slower).
        const B = opts.linkBatch || 1;
        for (let i = 0; i < g.items.length; i += B) {
          ctx.beginPath();
          for (let j = i; j < Math.min(i + B, g.items.length); j++) { const c = g.items[j]; ctx.moveTo(c.S[0], c.S[1]); ctx.quadraticCurveTo(c.C[0], c.C[1], c.E[0], c.E[1]); }
          ctx.stroke();
        }
      }
      ctx.setLineDash([]);
      for (const bl of down) {
        // A broken link: two halves with a gap and a cross in the middle.
        const c = bl.curve, gap = 0.07;
        ctx.globalAlpha = bl.alphaNow; ctx.strokeStyle = T.bad; ctx.lineWidth = 1.8; ctx.setLineDash([6, 4]);
        strokeCurve(c, 0, 0.5 - gap); strokeCurve(c, 0.5 + gap, 1);
        const [mx, my] = curvePoint(c, 0.5), s = 6;
        ctx.setLineDash([]); ctx.lineWidth = 2.2;
        ctx.beginPath(); ctx.moveTo(mx - s, my - s); ctx.lineTo(mx + s, my + s); ctx.moveTo(mx + s, my - s); ctx.lineTo(mx - s, my + s); ctx.stroke();
      }
      ctx.globalAlpha = 1;
      ctx.restore();
      st.labelled = labelled;
    }
    function drawFlow(dt) {
      const items = st.flowItems || [];
      const move = st.motion === 'lively' && st.flow;
      st.flowActive = move && items.length > 0;
      if (!items.length) return;
      const byColor = new Map();
      for (const it of items) {
        if (move) it.bl.phase = (it.bl.phase + dt * 70 / Math.max(60, it.c.len)) % 1;
        let d = byColor.get(it.color); if (!d) { d = []; byColor.set(it.color, d); }
        for (let i = 0; i < it.count; i++) d.push(curvePoint(it.c, (it.bl.phase + i / it.count) % 1));
      }
      const rad = Math.max(1.6, Math.min(3.2, 2.2 * Math.sqrt(cache.k || st.k)));
      ctx.save(); ctx.globalAlpha = 1;
      for (const [color, pts] of byColor) {
        ctx.fillStyle = color; ctx.beginPath();
        for (const p of pts) { ctx.moveTo(p[0] + rad, p[1]); ctx.arc(p[0], p[1], rad, 0, Math.PI * 2); }
        ctx.fill();
      }
      ctx.restore();
    }
    function strokeCurve(c, t0, t1) {
      ctx.beginPath();
      const steps = Math.max(4, Math.ceil((t1 - t0) * 16));
      for (let i = 0; i <= steps; i++) {
        const p = curvePoint(c, t0 + (t1 - t0) * (i / steps));
        if (i) ctx.lineTo(p[0], p[1]); else ctx.moveTo(p[0], p[1]);
      }
      ctx.stroke();
    }

    function drawTokens(t0) {
      const T = st.tokensCss;
      const keep = [];
      for (const tok of st.tokens) {
        const hopDur = tok.hopMs || 900;
        const el = t0 - tok.start;
        const hop = Math.floor(el / hopDur);
        if (hop >= tok.path.length - 1) { if (tok.done) tok.done(); continue; }
        keep.push(tok);
        const a = rep(st.scene.byId.get(tok.path[hop])), b = rep(st.scene.byId.get(tok.path[hop + 1]));
        if (a === b) continue;
        const bl = (st.bundles || []).find(x => (x.a === a && x.b === b) || (x.a === b && x.b === a));
        let p;
        const t = easeInOut((el - hop * hopDur) / hopDur);
        if (bl && bl.curve) p = curvePoint(bl.curve, bl.a === a ? t : 1 - t);
        else if (a.q && b.q) { const A = [(a.q[0][0] + a.q[2][0]) / 2, (a.q[0][1] + a.q[2][1]) / 2], B = [(b.q[0][0] + b.q[2][0]) / 2, (b.q[0][1] + b.q[2][1]) / 2]; p = [lerp(A[0], B[0], t), lerp(A[1], B[1], t)]; }
        else continue;
        const r = Math.max(5, Math.min(9, 7 * Math.sqrt(st.k)));
        ctx.save();
        ctx.fillStyle = T.paper; ctx.beginPath(); ctx.arc(p[0], p[1], r + 2.5, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = T[tok.color] || tok.color || T.link; ctx.beginPath(); ctx.arc(p[0], p[1], r, 0, Math.PI * 2); ctx.fill();
        if (tok.label && st.k > 0.35) {
          ctx.font = '600 11px ' + (T.font || 'system-ui');
          const w = ctx.measureText(tok.label).width + 12;
          ctx.fillStyle = T.paper; ctx.strokeStyle = T[tok.color] || tok.color || T.link; ctx.lineWidth = 1;
          ctx.beginPath(); ctx.roundRect ? ctx.roundRect(p[0] + r + 4, p[1] - 9, w, 18, 9) : ctx.rect(p[0] + r + 4, p[1] - 9, w, 18); ctx.fill(); ctx.stroke();
          ctx.fillStyle = T.ink; ctx.textAlign = 'left'; ctx.textBaseline = 'middle'; ctx.fillText(tok.label, p[0] + r + 10, p[1] + 0.5);
        }
        ctx.restore();
      }
      st.tokens = keep;
    }

    function drawEffects(t0) {
      const T = st.tokensCss;
      const keep = [];
      for (const fx of st.effects) {
        const p = (t0 - fx.start) / fx.dur;
        if (p >= 1 && !fx.sustain) continue;
        keep.push(fx);
        const n = st.scene.byId.get(fx.node);
        if (!n) continue;
        const r = rep(n);
        if (!r.q) continue;
        const q = r.q;
        ctx.save();
        if (fx.kind === 'pulse') {
          ctx.globalAlpha = (1 - p) * 0.9; ctx.strokeStyle = T[fx.color] || fx.color || T.link; ctx.lineWidth = 2;
          pathQuad(grow(q, 4 + p * 16), 12 * st.k); ctx.stroke();
        } else if (fx.kind === 'spin') {
          // A running step: an arc travelling around the node while the step runs.
          const cx = (q[0][0] + q[2][0]) / 2, cy = (q[0][1] + q[2][1]) / 2;
          const rad = Math.max(14, Math.min(40, Math.hypot(q[2][0] - q[0][0], q[2][1] - q[0][1]) * 0.18));
          const a0 = ((t0 - fx.start) / 900) * Math.PI * 2;
          ctx.strokeStyle = T[fx.color] || fx.color || T.link; ctx.lineWidth = 3; ctx.lineCap = 'round';
          const corner = q[1];
          ctx.beginPath(); ctx.arc(corner[0] - rad * 0.6, corner[1] + rad * 0.6, rad * 0.42, a0, a0 + Math.PI * 1.2); ctx.stroke();
          void cx; void cy;
        } else if (fx.kind === 'restart') {
          // Container restart: grey wash that drains away, then a short pop.
          ctx.globalAlpha = p < 0.6 ? 0.55 : 0.55 * (1 - (p - 0.6) / 0.4);
          ctx.fillStyle = T.surfaceStrong; pathQuad(q, 10 * st.k); ctx.fill();
          if (p > 0.6) { ctx.globalAlpha = 1 - (p - 0.6) / 0.4; ctx.strokeStyle = T.good; ctx.lineWidth = 2; pathQuad(grow(q, (p - 0.6) * 20), 12 * st.k); ctx.stroke(); }
        } else if (fx.kind === 'snap') {
          ctx.globalAlpha = 1 - p; ctx.strokeStyle = T.bad; ctx.lineWidth = 3;
          pathQuad(grow(q, 2 + p * 10), 12 * st.k); ctx.stroke();
        }
        ctx.restore();
      }
      st.effects = keep;
    }

    // ---------- hit testing ----------
    function hit(sx0, sy0) {
      // Hit shapes are in the cache's screen space; map the pointer into it.
      const s = st.k / cache.k, sx = (sx0 - (st.tx - cache.tx * s)) / s, sy = (sy0 - (st.ty - cache.ty * s)) / s;
      for (let i = st.hits.length - 1; i >= 0; i--) {
        const h = st.hits[i];
        if (h.n.alpha < 0.4) continue;
        if (inside(h.q, sx, sy)) return h.n;
      }
      return null;
    }
    function inside(q, x, y) {
      let c = false;
      for (let i = 0, j = 3; i < 4; j = i++) {
        if (((q[i][1] > y) !== (q[j][1] > y)) && (x < (q[j][0] - q[i][0]) * (y - q[i][1]) / (q[j][1] - q[i][1]) + q[i][0])) c = !c;
      }
      return c;
    }

    // ---------- events ----------
    const listeners = {};
    function on(evt, fn) { (listeners[evt] = listeners[evt] || []).push(fn); }
    function emit(evt, arg) { for (const fn of listeners[evt] || []) fn(arg); }

    function setHidden(h) { st.hidden = h; if (h) { if (st.raf) cancelAnimationFrame(st.raf); if (st.timer) clearTimeout(st.timer); st.raf = 0; st.timer = 0; st.last = 0; } else invalidate(); }
    function setOffscreen(o) { st.offscreen = o; if (o) { if (st.raf) cancelAnimationFrame(st.raf); if (st.timer) clearTimeout(st.timer); st.raf = 0; st.timer = 0; st.last = 0; } else invalidate(); }

    const api = {
      st, setScene, resize, fit, focusRect, zoomAt, panBy, animateTo, cameraFor, setIso, invalidate,
      toScreen, toWorld, hit, on, emit, rep, level, lineY, addToken, addEffect, removeEffect, text, pathQuad, quad, grow, curvePoint,
      setHidden, setOffscreen, isGroup,
      get proj() { return proj; },
      get ctx() { return ctx; },
      request, moved,
      setTokens(t) { st.tokensCss = t; widthCache.clear(); fitCache.clear(); invalidate(); },
      setMotion(m) { st.motion = m; if (m === 'off') { st.effects = []; st.tokens = []; } invalidate(); },
      setFlow(on) { st.flow = on; invalidate(); },
      // Synchronous frame for measurements.
      renderNow() { if (st.raf) cancelAnimationFrame(st.raf); if (st.timer) clearTimeout(st.timer); st.raf = 0; st.timer = 0; frame(now()); },
      setCamera(k, tx, ty) { st.tween = null; st.k = k; st.tx = tx; st.ty = ty; st.inMotion = true; settleSoon(); },
    };
    return api;
  }

  const api = { buildScene, createEngine, projector, LEAF, BOX, OPEN_AT, smooth, clamp };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.LSCEngine = api;
})(typeof window !== 'undefined' ? window : globalThis);
