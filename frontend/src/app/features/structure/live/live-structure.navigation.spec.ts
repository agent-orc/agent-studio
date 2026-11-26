import { describe, expect, it } from 'vitest';
import { LiveCamera } from './live-structure.camera';
import { levelOf } from './live-structure.facts';
import { liveSourcesFixture, liveStructureFixture } from './live-structure.fixtures';
import { layoutLiveScene } from './live-structure.layout';
import { LIVE_KIND_LABEL, buildLiveScene, type LiveNode, type LiveScene } from './live-structure.model';
import { LiveHistory, anchorOf, closestView, focusOf, jumpView, restoreAnchor, sameView, searchNodes } from './live-structure.navigation';

function stage(width = 790, height = 690, data = liveStructureFixture): { scene: LiveScene; cam: LiveCamera } {
  const scene = buildLiveScene(data, liveSourcesFixture), cam = new LiveCamera();
  cam.reduced = true;
  cam.measure(width, height, { left: 0, top: 44, right: 0, bottom: 48 }, false);
  cam.setBounds(layoutLiveScene(scene, width < 720 ? 'tall' : 'wide', width / height).bounds);
  cam.fitAll(true);
  return { scene, cam };
}
const onScreen = (cam: LiveCamera, node: LiveNode): { x: number; y: number; w: number; h: number } =>
  ({ x: cam.sx(node.rect.x), y: cam.sy(node.rect.y), w: node.rect.w * cam.k, h: node.rect.h * cam.k });
const leaf = (node: LiveNode): boolean => node.kind !== 'boundary' && !((node.kind === 'machine' || node.kind === 'vm' || node.kind === 'group') && node.children.length);

describe('live structure navigation (AGT-4024)', () => {
  for (const [width, height] of [[790, 690], [1100, 760], [390, 500]] as const) {
    it(`one jump brings any node into view at the container level (${width} x ${height})`, () => {
      const { scene, cam } = stage(width, height);
      for (const node of scene.order.filter(leaf)) {
        cam.fitAll(true);
        cam.go(jumpView(cam, node));
        const box = onScreen(cam, node);
        expect(levelOf(node, box.w, box.h, cam.k), node.id).toBe(3);
        // Its start is in view; a storage strip as wide as its machine may run out of a narrow pane.
        expect(box.x, node.id).toBeGreaterThanOrEqual(-0.5);
        expect(box.y, node.id).toBeGreaterThanOrEqual(cam.safe.top - 0.5);
        if (node.kind !== 'disk') {
          expect(box.x + box.w, node.id).toBeLessThanOrEqual(width + 0.5);
          expect(box.y + box.h, node.id).toBeLessThanOrEqual(height - cam.safe.bottom + 0.5);
        }
      }
    });
  }

  it('shows a machine whole, and the closest view of a container is closer than the jump', () => {
    const { scene, cam } = stage();
    const machine = scene.byId.get('machine:studio-server')!, unit = scene.byId.get('machine:review-3')!;
    cam.go(jumpView(cam, machine));
    const box = onScreen(cam, machine);
    expect(box.w).toBeLessThanOrEqual(790);
    expect(box.h).toBeLessThanOrEqual(690 - 92);
    expect(focusOf(cam, scene).id).toBe('machine:studio-server');
    const near = jumpView(cam, unit), closest = closestView(cam, unit);
    expect(closest.k).toBeGreaterThan(near.k * 1.3);
    expect(sameView(near, closest)).toBe(false);
    expect(levelOf(unit, unit.rect.w * closest.k, unit.rect.h * closest.k, closest.k)).toBe(4);
    // A service inside a container is shown with its container.
    expect(jumpView(cam, scene.byId.get('component:review-3:review-3-runner')!)).toEqual(near);
  });

  it('keeps the view on the node it rests on when the data brings a new arrangement', () => {
    const { scene, cam } = stage();
    const unit = scene.byId.get('machine:review-3')!;
    cam.go(jumpView(cam, unit));
    cam.nudge(37, -21);
    const before = onScreen(cam, unit), k = cam.k;
    const anchor = anchorOf(cam, scene);
    expect(anchor[0].id).toBe('machine:review-3');
    // A container appears on another machine, which grows and pushes the machine below it down.
    const grown = { ...liveStructureFixture, revision: 42, nodes: [...liveStructureFixture.nodes, ...['cache', 'queue', 'mail'].map(name => ({
      ...liveStructureFixture.nodes.find(item => item.id === 'component:studio-server:web')!, id: `component:studio-server:${name}`, label: name, unitId: name }))] };
    const next = buildLiveScene(grown, liveSourcesFixture);
    cam.setBounds(layoutLiveScene(next, 'wide', 790 / 690).bounds);
    const moved = next.byId.get('machine:review-3')!;
    expect(Math.abs(moved.rect.x - unit.rect.x) + Math.abs(moved.rect.y - unit.rect.y)).toBeGreaterThan(20);
    restoreAnchor(cam, next, anchor);
    const after = onScreen(cam, moved);
    expect(cam.k).toBe(k);
    expect(after.x).toBeCloseTo(before.x, 3);
    expect(after.y).toBeCloseTo(before.y, 3);
    // The node the view rested on is gone: the view stays with what held it.
    const without = buildLiveScene({ ...liveStructureFixture, nodes: liveStructureFixture.nodes.filter(item => !item.id.includes('review-3')) }, liveSourcesFixture);
    layoutLiveScene(without, 'wide', 790 / 690);
    restoreAnchor(cam, without, anchor);
    const vm = without.byId.get('machine:linux-vm')!;
    expect(cam.x).toBeGreaterThanOrEqual(vm.rect.x);
    expect(cam.x).toBeLessThanOrEqual(vm.rect.x + vm.rect.w);
  });

  it('remembers the way back, one stop per jump', () => {
    const history = new LiveHistory();
    history.push({ view: { x: 10, y: 10, k: 1 }, selected: null });
    history.push({ view: { x: 10, y: 10, k: 1 }, selected: null });
    history.push({ view: { x: 400, y: 10, k: 2 }, selected: 'a' });
    expect(history.length).toBe(2);
    expect(history.pop()).toEqual({ view: { x: 400, y: 10, k: 2 }, selected: 'a' });
    expect(history.pop()?.selected).toBeNull();
    expect(history.pop()).toBeNull();
  });

  it('finds nodes by name, role, kind and reported name, containers first', () => {
    const { scene } = stage();
    const find = (query: string): string[] => searchNodes(scene, query, LIVE_KIND_LABEL).map(node => node.name);
    expect(find('review 3')[0]).toBe('Review host 3');
    expect(find('gate')[0]).toBe('Gate host');
    expect(find('review-2')).toContain('Review host 2');
    expect(find('api')[0]).toBe('API');
    expect(find('storage').length).toBeGreaterThan(0);
    expect(find('backup')).toContain('Off-host backup target');
    expect(find('no such thing')).toEqual([]);
    expect(find('  ')).toEqual([]);
    expect(searchNodes(scene, 'e', LIVE_KIND_LABEL).length).toBe(8);
  });
});

describe('live structure camera (AGT-4024)', () => {
  const run = (cam: LiveCamera, seconds = 1): void => { for (let t = 0; t < seconds * 60; t++) cam.step(performance.now() + t * 16, 0.016); };

  it('zooms about the pointer: the world point under it stays under it', () => {
    const { cam } = stage();
    cam.reduced = false;
    const wx = cam.wx(600), wy = cam.wy(200);
    cam.zoomAt(600, 200, 2.5);
    expect(cam.view.k).toBeCloseTo(cam.k * 2.5, 5);
    run(cam);
    expect(cam.moving).toBe(false);
    expect(cam.sx(wx)).toBeCloseTo(600, 1);
    expect(cam.sy(wy)).toBeCloseTo(200, 1);
  });

  it('moves in eased keyboard steps that add up while a key is held, and glides after a drag', () => {
    const { cam } = stage();
    cam.reduced = false;
    cam.zoomAt(cam.ox, cam.oy, 3);
    run(cam);
    const x = cam.x, k = cam.k;
    for (let i = 0; i < 3; i++) { cam.panBy(90, 0); cam.step(performance.now(), 0.016); }
    expect(cam.x).toBeGreaterThan(x);
    expect(cam.x).toBeLessThan(x + 270 / k);
    expect(cam.view.x).toBeCloseTo(x + 270 / k, 5);
    run(cam);
    expect(cam.x).toBeCloseTo(x + 270 / k, 5);
    expect(cam.k).toBe(k);
    cam.throw(-600, 0);
    run(cam, 2);
    expect(cam.moving).toBe(false);
    expect(cam.x).toBeGreaterThan(x + 270 / k);
  });

  it('flies to a view briefly and applies it at once with reduced motion', () => {
    const { scene, cam } = stage();
    const to = jumpView(cam, scene.byId.get('machine:gate-1')!);
    cam.reduced = false;
    const t0 = performance.now();
    cam.go(to);
    expect(cam.view).toEqual(to);
    cam.step(t0 + 100, 0.016);
    expect(cam.moving).toBe(true);
    cam.step(t0 + 460, 0.016);
    expect(cam.moving).toBe(false);
    expect([cam.x, cam.y, cam.k]).toEqual([to.x, to.y, to.k]);
    cam.reduced = true;
    cam.fitAll();
    expect(cam.moving).toBe(false);
  });
});
