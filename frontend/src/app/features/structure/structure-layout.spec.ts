import { describe, expect, it } from 'vitest';
import { layoutStructure, leafDetail, linkCrossings, type StructureLayout } from './structure-layout';
import { complexFixture, complexServices, structureFixtures } from './structure.fixtures';
import { projectStructure, summaryText, visibleBoxes, type StructureData } from './structure.model';
import { structureTestClockMs } from './structure-test-clock';

// Canvas width of the structure view at a 1440 px screen: shell navigation,
// outline column and padding taken off, rounded down.
const SCREEN_1440_CANVAS = 880;
const lay = (data: StructureData, view: 'host' | 'container', level: 1 | 2 | 3, width = SCREEN_1440_CANVAS): StructureLayout =>
  layoutStructure(projectStructure(data, view, level, {}), data, view, width);

describe('Structure layout and routing', () => {
  for (const [name, fixture] of Object.entries(structureFixtures))
    for (const view of ['host', 'container'] as const)
      for (const level of [1, 2] as const)
        it(`routes no link across an instance box: ${name}, ${view} view, level ${level}`, () => {
          const layout = lay(fixture, view, level);
          expect(linkCrossings(layout)).toEqual([]);
          expect(layout.width).toBeLessThanOrEqual(SCREEN_1440_CANVAS);
        });

  it('draws links in every checked fixture so the geometry check is not vacuous', () => {
    for (const [name, fixture] of Object.entries(structureFixtures)) {
      expect(lay(fixture, 'host', 2).links.length, name).toBeGreaterThan(2);
      expect(lay(fixture, 'host', 1).links.length, name).toBeGreaterThan(1);
    }
  });

  it('fits the complex fixture at level 1 into one 1440 px screen', () => {
    const layout = lay(complexFixture, 'host', 1);
    expect(new Set(complexFixture.nodes.filter(node => node.kind === 'container').map(node => node.label)).size)
      .toBe(Object.values(complexServices).flat().length);
    expect(Object.values(complexServices).flat()).toHaveLength(60);
    expect(layout.width).toBeLessThanOrEqual(SCREEN_1440_CANVAS);
    expect(layout.height).toBeLessThanOrEqual(640);
    const kinds = new Set(layout.boxes.map(box => box.box.kind));
    for (const kind of ['region', 'cluster', 'managed', 'external', 'environment']) expect(kinds).toContain(kind);
  });

  // Median of five warm runs, projection included, so one garbage-collection pause does not decide.
  const medianMs = (run: () => unknown): number => {
    run();
    const timings = Array.from({ length: 5 }, () => {
      const start = structureTestClockMs();
      run();
      return structureTestClockMs() - start;
    }).sort((p, q) => p - q);
    return timings[2];
  };

  it('lays out the complex fixture at level 1 in under 50 ms', () => {
    for (const view of ['host', 'container'] as const)
      expect(medianMs(() => lay(complexFixture, view, 1))).toBeLessThan(50);
  });

  it('lays out and routes the fully open complex fixture within the budget', () => {
    expect(medianMs(() => lay(complexFixture, 'host', 3, 1600))).toBeLessThan(50);
    expect(lay(complexFixture, 'host', 3, 1600).boxes.length).toBeGreaterThan(200);
  });

  it('is deterministic for the same data and width', () => {
    expect(lay(complexFixture, 'host', 2)).toEqual(lay(complexFixture, 'host', 2));
  });

  it('merges a unit across its placements in the container view', () => {
    const roots = projectStructure(complexFixture, 'container', 2, {});
    const web = visibleBoxes(roots).filter(box => box.label === 'web' && box.environmentId === 'production');
    expect(web).toHaveLength(1);
    expect(web[0].replicas).toBe(8);
    expect(web[0].placements).toBe(2);
    expect(web[0].members).toEqual(['eu-storefront-web', 'us-storefront-web']);
    expect(leafDetail(web[0])).toBe('2 placements');
    expect(leafDetail(web[0], 'container')).toBe('production · 2 placements');
    const staging = visibleBoxes(roots).find(box => box.label === 'web' && box.environmentId === 'staging')!;
    expect(leafDetail(staging, 'container')).toBe('staging · Container');
    const opened = visibleBoxes(projectStructure(complexFixture, 'container', 3, {}))
      .filter(box => box.label === 'web' && box.environmentId === 'production');
    expect(opened).toHaveLength(2);
  });

  it('keeps links to merged members attached to the merged box', () => {
    const layout = lay(complexFixture, 'container', 2);
    const https = layout.links.find(link => link.label === 'HTTPS');
    expect(https?.to).toBe('function:storefront');
    expect(https?.count).toBe(2);
  });

  it('attaches host-view links to the nearest visible placement', () => {
    const layout = lay(complexFixture, 'host', 1);
    expect(layout.links.find(link => link.label === 'HTTPS')?.to).toBe('eu-k8s');
    expect(layout.links.filter(link => link.label === 'HTTPS').map(link => link.to)).toEqual(['eu-k8s', 'us-k8s']);
    expect(layout.links.map(link => link.number)).toEqual(layout.links.map((_, i) => i + 1));
  });

  it('words collapsed summaries with correct plurals', () => {
    const web = visibleBoxes(projectStructure(structureFixtures.workspace, 'host', 2, {}))
      .find(box => box.id === 'machine:web')!;
    expect(web.collapsed).toBe(true);
    expect(summaryText(web)).toBe('2 processes · 1 container');
  });

  it('labels managed services by their declared observation', () => {
    const boxes = visibleBoxes(projectStructure(complexFixture, 'host', 1, {}));
    expect(leafDetail(boxes.find(box => box.id === 'eu-db')!)).toBe('declared · probed via TLS');
    expect(leafDetail(boxes.find(box => box.id === 'eu-obj')!)).toBe('declared · probed via HTTP');
    expect(leafDetail(boxes.find(box => box.id === 'eu-mq')!)).toBe('declared · not observed');
  });

  it('places externals in a column outside the boundary and in a row on narrow canvases', () => {
    const wide = lay(structureFixtures.agt, 'host', 2);
    const boundary = wide.boxes.find(box => box.id === 'b')!;
    const github = wide.boxes.find(box => box.id === 'github')!;
    expect(github.x).toBeGreaterThanOrEqual(boundary.x + boundary.w);
    const narrow = lay(structureFixtures.agt, 'host', 2, 560);
    const below = narrow.boxes.find(box => box.id === 'github')!;
    expect(below.y).toBeGreaterThanOrEqual(narrow.boxes.find(box => box.id === 'b')!.h);
    expect(linkCrossings(narrow)).toEqual([]);
  });

  it('nests every box inside its parent with a gutter', () => {
    for (const fixture of Object.values(structureFixtures)) {
      const layout = lay(fixture, 'host', 3, 1400);
      const byId = new Map(layout.boxes.map(box => [box.id, box]));
      for (const box of layout.boxes) {
        const parent = box.parentId ? byId.get(box.parentId) : null;
        if (!parent) continue;
        expect(box.x).toBeGreaterThanOrEqual(parent.x + 10);
        expect(box.x + box.w).toBeLessThanOrEqual(parent.x + parent.w - 10);
        expect(box.y + box.h).toBeLessThanOrEqual(parent.y + parent.h - 10);
      }
    }
  });
});
