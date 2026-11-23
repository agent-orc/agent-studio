// Shared benchmark driver. Each candidate entry exposes an adapter with
// init(host, scene), camera(k, tx, ty), render() and flow(on). The driver
// runs a scripted pan and zoom with every node and link of the synthetic
// scene in play and records frame intervals and synchronous render time.
import { synthScene } from './scene.js';
import engine from '../src/engine.js';

export function install(name, adapter) {
  const model = synthScene();
  // One layout for every candidate: the engine's nested layout.
  const scene = engine.buildScene(model);
  const W = 1280, H = 800;
  const fitK = Math.min(W / scene.bounds.w, H / scene.bounds.h) * 0.94;
  const cam = t => {
    // Zoom between fit and 3x fit while drifting across the scene.
    const z = 1 + 2 * Math.sin(Math.PI * t) ** 2;
    const k = fitK * z;
    const cx = scene.bounds.w * (0.5 + 0.25 * Math.sin(2 * Math.PI * t));
    const cy = scene.bounds.h * (0.5 + 0.2 * Math.cos(2 * Math.PI * t));
    return [k, W / 2 - cx * k, H / 2 - cy * k];
  };
  window.BENCH = {
    name,
    async init() {
      const host = document.getElementById('host');
      host.style.width = W + 'px'; host.style.height = H + 'px';
      const t0 = performance.now();
      await adapter.init(host, scene, model, { W, H });
      adapter.camera(...cam(0));
      await adapter.render();
      await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
      return { firstRenderMs: +(performance.now() - t0).toFixed(1), nodes: scene.nodes.length, links: scene.links.length };
    },
    panZoom(ms) {
      return new Promise(resolve => {
        const frames = [], work = [];
        const start = performance.now();
        const r0 = adapter.frames ? adapter.frames() : 0;
        let last = start;
        const step = ts => {
          const t = (ts - start) / ms;
          if (t >= 1) {
            const sorted = [...frames].sort((a, b) => a - b), w = [...work].sort((a, b) => a - b);
            const q = (arr, p) => arr[Math.min(arr.length - 1, Math.floor(arr.length * p))];
            resolve({
              frames: frames.length, fps: +(frames.length / (ms / 1000)).toFixed(1),
              renderFps: adapter.frames ? +((adapter.frames() - r0) / (ms / 1000)).toFixed(1) : null,
              frameP50: +q(sorted, 0.5).toFixed(1), frameP95: +q(sorted, 0.95).toFixed(1),
              slowShare: +(frames.filter(f => f > 20).length / frames.length).toFixed(3),
              renderP50: +q(w, 0.5).toFixed(2), renderP95: +q(w, 0.95).toFixed(2),
            });
            return;
          }
          frames.push(ts - last); last = ts;
          const w0 = performance.now();
          adapter.camera(...cam(t));
          adapter.render();
          work.push(performance.now() - w0);
          requestAnimationFrame(step);
        };
        requestAnimationFrame(step);
      });
    },
    flow(on) { adapter.camera(...cam(0)); adapter.flow(on); },
    frameCounter() { return adapter.frames ? adapter.frames() : null; },
  };
}
