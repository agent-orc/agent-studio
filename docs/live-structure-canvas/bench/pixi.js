// Candidate B: PixiJS 8 (WebGL 2D). Static geometry is built once; pan and
// zoom move the world container; the ticker is off so frames render on demand.
import { Application, Container, Graphics, BitmapText } from 'pixi.js';
import { install } from './harness.js';

let app, world, flowG, curves = [], flowOn = false, frames = 0, timer = 0, phase = 0, lastT = 0;
const curveOf = (l, scene) => {
  const a = l.a, b = l.b;
  const S = [a.x + a.w / 2, a.y + a.h / 2], E = [b.x + b.w / 2, b.y + b.h / 2];
  const dx = E[0] - S[0], dy = E[1] - S[1], len = Math.hypot(dx, dy) || 1, bend = Math.min(60, len * 0.12);
  return { S, E, C: [(S[0] + E[0]) / 2 - dy / len * bend, (S[1] + E[1]) / 2 + dx / len * bend], len, traffic: l.traffic };
};
const pt = (c, t) => { const u = 1 - t; return [u * u * c.S[0] + 2 * u * t * c.C[0] + t * t * c.E[0], u * u * c.S[1] + 2 * u * t * c.C[1] + t * t * c.E[1]]; };
function drawFlow(dt) {
  phase = (phase + dt * 0.4) % 1;
  flowG.clear();
  for (const c of curves) {
    const n = Math.max(1, Math.round(c.len / 90 * c.traffic / 1000 * 3));
    for (let i = 0; i < n; i++) { const p = pt(c, (phase + i / n) % 1); flowG.circle(p[0], p[1], 2.2); }
  }
  flowG.fill({ color: 0x5f5e59 });
}
install('pixi-webgl', {
  async init(host, scene, model, { W, H }) {
    app = new Application();
    await app.init({ width: W, height: H, antialias: true, autoStart: false, background: '#fcfcfb', resolution: 1, preference: 'webgl' });
    host.appendChild(app.canvas);
    world = new Container();
    app.stage.addChild(world);
    const groups = new Graphics(), leaves = new Graphics(), links = new Graphics();
    for (const n of scene.nodes) {
      const g = n.kind === 'container' ? leaves : groups;
      g.roundRect(n.x, n.y, n.w, n.h, n.kind === 'site' ? 16 : 10).fill({ color: n.kind === 'container' ? 0xfcfcfb : n.kind === 'site' ? 0xf7f6f3 : 0xf2f1ee }).stroke({ width: 1, color: 0xd8d6cf });
    }
    for (const l of scene.links) { const c = curveOf(l, scene); curves.push(c); links.moveTo(c.S[0], c.S[1]).quadraticCurveTo(c.C[0], c.C[1], c.E[0], c.E[1]); }
    links.stroke({ width: 1.1, color: 0x5f5e59, alpha: 0.9 });
    world.addChild(groups, links, leaves);
    for (const n of scene.nodes) {
      const t = new BitmapText({ text: n.label, style: { fontFamily: 'Arial', fontSize: n.kind === 'site' ? 19 : 13.5, fill: 0x151515 } });
      t.x = n.x + 14; t.y = n.y + 12;
      world.addChild(t);
    }
    flowG = new Graphics();
    world.addChild(flowG);
  },
  camera(k, tx, ty) { world.scale.set(k); world.position.set(tx, ty); },
  render() { app.render(); frames++; },
  flow(on) {
    flowOn = on; clearTimeout(timer);
    if (!on) { flowG.clear(); app.render(); return; }
    lastT = performance.now();
    const tick = () => {
      if (!flowOn) return;
      const t = performance.now(); drawFlow((t - lastT) / 1000); lastT = t;
      app.render(); frames++;
      timer = setTimeout(() => requestAnimationFrame(tick), 33);
    };
    requestAnimationFrame(tick);
  },
  frames() { return frames; },
});
