import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LiveCamera } from './live-structure.camera';
import { liveSourcesFixture, liveStructureFixture } from './live-structure.fixtures';
import { layoutLiveScene } from './live-structure.layout';
import { LIVE_LAYERS, buildLiveScene, type LiveLayer, type LiveNode } from './live-structure.model';
import { closestView, jumpView } from './live-structure.navigation';
import type { LivePalette } from './live-structure.painter';
import { LiveRenderer } from './live-structure.renderer';
import { NO_SOURCES } from './live-structure.sources';

/** A 2D context that measures text by its length and font size and records what is written. */
function fakeContext(written: string[]): CanvasRenderingContext2D {
  const state: Record<string, unknown> = { font: '400 11px mono' };
  return new Proxy(state, {
    get: (target, key: string) => {
      if (key === 'measureText') return (text: string) => ({ width: text.length * Number(/([\d.]+)px/.exec(String(target['font']))?.[1] ?? 11) * 0.6 });
      if (key === 'fillText') return (text: string) => { written.push(text); };
      if (key === 'createPattern') return () => ({ setTransform: () => undefined });
      return key in target ? target[key] : () => undefined;
    },
    set: (target, key: string, value) => { target[key] = value; return true; },
  }) as unknown as CanvasRenderingContext2D;
}
const palette = new Proxy({}, { get: (_, key: string) => key.startsWith('font') ? 'sans' : '#000' }) as LivePalette;
const W = 790, H = 690;

function setup(sources = liveSourcesFixture) {
  const written: string[] = [];
  const context = fakeContext(written);
  const scene = buildLiveScene(liveStructureFixture, sources), cam = new LiveCamera();
  cam.reduced = true;
  cam.measure(W, H, { left: 0, top: 44, right: 0, bottom: 48 }, false);
  const layout = layoutLiveScene(scene, 'wide', W / H);
  cam.setBounds(layout.bounds);
  const renderer = new LiveRenderer({ getContext: () => context, width: 0, height: 0 } as unknown as HTMLCanvasElement, cam);
  renderer.setPalette(palette);
  renderer.resize(W, H);
  renderer.setScene(scene, layout);
  const draw = (on: readonly LiveLayer[]): void => {
    written.length = 0;
    renderer.draw({ layers: Object.fromEntries(LIVE_LAYERS.map(layer => [layer.id, on.includes(layer.id)])) as Record<LiveLayer, boolean>,
      order: [...on].reverse(), selected: null, selectedLink: null, hover: null, hoverLink: null });
  };
  return { scene, cam, renderer, draw, written };
}
const subsets: LiveLayer[][] = Array.from({ length: 32 }, (_, bits) => LIVE_LAYERS.map(layer => layer.id).filter((_, index) => bits & (1 << index)));
const inView = (cam: LiveCamera, node: LiveNode): boolean => cam.sx(node.rect.x) >= 0 && cam.sx(node.rect.x + node.rect.w) <= W
  && cam.sy(node.rect.y) >= 0 && cam.sy(node.rect.y + node.rect.h) <= H;

describe('live structure renderer (AGT-4024)', () => {
  beforeEach(() => {
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation((() => fakeContext([])) as never);
    vi.stubGlobal('DOMMatrix', class { scale(): unknown { return this; } });
  });
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

  it('never draws a bare box at the container level or closer, with any combination of layers', () => {
    for (const sources of [liveSourcesFixture, NO_SOURCES]) {
      const { scene, cam, renderer, draw } = setup(sources);
      const targets = scene.order.filter(node => node.kind !== 'boundary');
      let checked = 0;
      for (const target of targets) for (const view of [jumpView(cam, target), closestView(cam, target)]) {
        cam.go(view, true);
        for (const on of subsets) {
          draw(on);
          for (const node of targets) {
            if ((renderer.levels.get(node.id) ?? 0) < 3 || !inView(cam, node)) continue;
            expect(renderer.shown.get(node.id) ?? 0, `${node.id} with [${on.join(',')}] while on ${target.id}`).toBeGreaterThan(0);
            checked++;
          }
        }
      }
      expect(checked).toBeGreaterThan(targets.length * 32);
    }
  });

  it('says what is not observed and why on a node the Studio knows only by name', () => {
    const { scene, cam, draw, written } = setup();
    cam.go(closestView(cam, scene.byId.get('machine:spare')!), true);
    draw([]);
    const text = written.join(' ');
    expect(text).toContain('Not observed: state.');
    expect(text).toContain('no host reports from it.');
    // A container nobody reports from inside lists what is missing next to what is known.
    cam.go(closestView(cam, scene.byId.get('component:studio-server:api')!), true);
    draw(['perf']);
    const api = written.join(' ');
    expect(api).toContain('2.0');
    expect(api).toMatch(/Not observed: cpu, memory/);
    expect(api).toMatch(/Not observed: ports, image, restarts, uptime, current job, last deploy/);
    expect(written.some(line => line.includes('…'))).toBe(false);
  });

  it('adds facts with every level and leads with the layer switched on last', () => {
    const { scene, cam, renderer, draw, written } = setup();
    const host = scene.byId.get('machine:review-1')!;
    const at = (k: number, on: readonly LiveLayer[]): string[] => {
      cam.go({ x: host.rect.x + host.rect.w / 2, y: host.rect.y + host.rect.h / 2, k }, true);
      draw(on);
      return [...written];
    };
    const counts = [0.45, 0.8, 1.5, 2.6].map(k => { at(k, ['perf', 'network', 'warnings']); return [renderer.levels.get(host.id), renderer.shown.get(host.id) ?? 0]; });
    expect(counts.map(item => item[0])).toEqual([1, 2, 3, 4]);
    expect(counts[0][1]).toBe(0);
    expect(counts[2][1]).toBeGreaterThan(counts[1][1] as number);
    expect(counts[3][1]).toBeGreaterThan(counts[2][1] as number);
    expect(at(1.5, [])).toEqual(expect.arrayContaining(['image', 'version', 'restarts', 'uptime', 'current job']));
    const perfLast = at(1.5, ['network', 'perf']), networkLast = at(1.5, ['perf', 'network']);
    expect(perfLast.indexOf('cpu')).toBeLessThan(perfLast.indexOf('links'));
    expect(networkLast.indexOf('links')).toBeLessThan(networkLast.indexOf('cpu'));
  });
});
