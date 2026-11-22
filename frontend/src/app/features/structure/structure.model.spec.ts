import { describe, expect, it } from 'vitest';
import { applyStructureDelta, factLines, isStructuralDelta, isUndeclared, observationGlyph, observationWord,
  projectStructure, visibleBoxes,
  type StructureData, type StructureDelta, type StructureNode, type StructureObservation } from './structure.model';
import { observedStructure } from './structure.fixtures';
import { structureTestClockMs } from './structure-test-clock';

const node = (id: string, kind: string, label: string, parentId: string | null,
  role: string | null = null): StructureNode => ({ id, kind, label, parentId, role,
    unitId: null, environmentId: null, project: null, provenance: 'declared', sourceRef: 'fixture' });

const fixtures: StructureData[] = [
  { nodes: [node('b', 'boundary', 'Website', null), node('vm', 'vm', 'Web VM', 'b'),
    node('site', 'container', 'Website', 'vm', 'web')], links: [] },
  { nodes: [node('b', 'boundary', 'AGT installation', null),
    node('cloud', 'location', 'Cloud provider', 'b'),
    node('site', 'location', 'Operator site', 'b'),
    node('home', 'location', 'Home lab', 'b'),
    node('studio', 'physical', 'Studio machine', 'site'),
    node('runner', 'cloud-server', 'Linux runner host', 'cloud'),
    node('lab', 'physical', 'Home-lab physical machine', 'home'),
    node('vm', 'vm', 'Home-lab VM', 'lab'),
    ...[1, 2, 3].map(n => node(`review-${n}`, 'container', `Review host ${n}`, 'vm', 'execution')),
    node('gate', 'container', 'Build and test gate', 'vm', 'verification'),
    node('github', 'external', 'GitHub', null),
    node('registry', 'external', 'Container registry', null),
    node('models', 'external', 'Model providers', null)], links: [] },
  { nodes: [node('b', 'boundary', 'Large', null), node('cluster', 'cluster', 'Cluster', 'b'),
    ...Array.from({ length: 200 }, (_, i) => node(`c-${i}`, 'container', `Service ${i}`, 'cluster', 'service'))], links: [] },
];

describe('Structure projection', () => {
  it('renders the AGT placement and function groups', () => {
    const host = projectStructure(fixtures[1], 'host', 3, {});
    expect(visibleBoxes(host).filter(n => n.kind === 'location')).toHaveLength(3);
    expect(visibleBoxes(host).filter(n => ['physical', 'cloud-server'].includes(n.kind))).toHaveLength(3);
    expect(visibleBoxes(host).filter(n => n.kind === 'container')).toHaveLength(4);
    expect(visibleBoxes(host).filter(n => n.kind === 'external')).toHaveLength(3);
    expect(visibleBoxes(host).find(n => n.id === 'vm')?.summary).toEqual({ container: 4 });
    const containers = projectStructure(fixtures[1], 'container', 3, {});
    expect(visibleBoxes(containers).filter(n => n.kind === 'group').map(n => n.label))
      .toEqual(['execution', 'verification']);
    expect(visibleBoxes(containers).filter(n => n.kind === 'container')).toHaveLength(4);
    expect(visibleBoxes(containers).filter(n => n.kind === 'location')).toHaveLength(0);
  });

  it('each collapsed summary equals its expanded children in all fixtures', () => {
    for (const fixture of fixtures) for (const view of ['host', 'container'] as const)
      for (const level of [1, 2, 3] as const) {
        const roots = projectStructure(fixture, view, level, {});
        const expanded = new Map(visibleBoxes(projectStructure(fixture, view, 3, {})).map(n => [n.id, n]));
        const countChildren = (box: typeof roots[number]): Record<string, number> => {
          const counts: Record<string, number> = {};
          for (const child of box.children) {
            if (['vm', 'cluster', 'namespace'].includes(child.kind))
              counts[child.kind] = (counts[child.kind] || 0) + 1;
            if (child.children.length) {
              for (const [kind, count] of Object.entries(countChildren(child)))
                counts[kind] = (counts[kind] || 0) + count;
            } else if (!['boundary', 'location', 'environment', 'group', 'physical', 'cloud-server'].includes(child.kind))
              counts[child.kind] = (counts[child.kind] || 0) + child.replicas;
          }
          return counts;
        };
        const check = (box: typeof roots[number]): void => {
          const opened = expanded.get(box.id);
          if (opened) expect(box.summary).toEqual(countChildren(opened));
          box.children.forEach(check);
        };
        roots.forEach(check);
      }
  });

  it('projects 200 nodes within the layout budget', () => {
    const start = structureTestClockMs();
    const boxes = projectStructure(fixtures[2], 'host', 3, {});
    expect(visibleBoxes(boxes)).toHaveLength(202);
    expect(structureTestClockMs() - start).toBeLessThan(50);
  });

  it('treats an unknown machine kind as a placement box', () => {
    const fixture: StructureData = { nodes: [node('b', 'boundary', 'Boundary', null),
      node('machine:custom', 'new-runtime', 'Custom runtime', 'b'),
      node('app', 'container', 'Application', 'machine:custom', 'service')], links: [] };
    expect(visibleBoxes(projectStructure(fixture, 'host', 1, {})).map(box => box.id))
      .toEqual(['b', 'machine:custom']);
    expect(visibleBoxes(projectStructure(fixture, 'container', 2, {})).map(box => box.id))
      .toContain('app');
  });

  it.each([
    ['matched', 'up', 'Up', '●'],
    ['declared-only', 'not observed', 'Not observed', '○'],
    ['undeclared', 'up', 'Up', '●'],
    ['undeclared', 'not observed', 'Not observed', '○'],
    ['differs', 'up', 'Differs', '▲'],
    ['stale', 'stale', 'Stale', '○'],
    ['unreachable-by-design', 'not observed', 'Unreachable by design', '○'],
  ] as const)('labels %s with a word and glyph', (difference, health, word, glyph) => {
    const observation: StructureObservation = { difference, health };
    expect(observationWord(observation)).toBe(word);
    expect(observationGlyph(observation)).toBe(glyph);
    const fixture: StructureData = { nodes: [{ ...node('host', 'physical', 'Host', null), observation }], links: [] };
    expect(visibleBoxes(projectStructure(fixture, 'host', 3, {}))[0].observation).toEqual(observation);
  });

  it('does not call an unknown health metric healthy', () => {
    expect(observationWord({ difference: 'matched', health: 'unknown' })).toBe('Not observed');
  });

  it('draws a system environment boundary around its exclusive shared placement', () => {
    const fixture: StructureData = { nodes: [node('installation', 'boundary', 'Workspace', null),
      node('system:product:production', 'system', 'Product · production', 'installation'),
      node('machine:web', 'vm', 'Web placement', 'installation'),
      { ...node('website', 'process', 'Website', 'machine:web'), project: 'PROJ-002', systemBoundaryId: 'system:product:production' },
      { ...node('agent', 'process', 'Run-target agent', 'machine:web'), project: 'PROJ-001', systemBoundaryId: 'system:product:production' }], links: [] };
    const roots = projectStructure(fixture, 'host', 3, {});
    expect(roots[0].children[0].children.map(child => child.id)).toEqual(['machine:web']);
    expect(fixture.nodes.find(item => item.id === 'machine:web')?.parentId).toBe('installation');
  });

  it('applies a delta in place and tells a fact update from a changed shape (AGT-3954)', () => {
    const base: StructureData = { revision: 7, nodes: [node('a', 'physical', 'A', null), node('b', 'physical', 'B', null)],
      links: [{ id: 'link:a>b:call', from: 'a', to: 'b', kind: 'call', label: 'Call', sourceRef: 'fixture' }] };
    const delta = (change: Partial<StructureDelta>): StructureDelta => ({ scope: 'workspace', environment: null, baseRevision: 7,
      revision: 8, generatedAt: '2026-11-17T12:00:00Z', nodes: [], removedNodes: [], links: [], removedLinks: [], ...change });
    const update = delta({ nodes: [{ ...base.nodes[1], label: 'B2' }],
      links: [{ ...base.links[0], facts: { state: 'up', latencyMs: 3 } }] });
    expect(isStructuralDelta(base, update)).toBe(false);
    const updated = applyStructureDelta(base, update);
    expect(updated.nodes.map(item => item.label)).toEqual(['A', 'B2']);
    expect(updated.links[0].facts?.latencyMs).toBe(3);
    expect(updated.revision).toBe(8);
    expect(base.nodes[1].label).toBe('B');

    const shape = delta({ nodes: [node('c', 'physical', 'C', null)], removedNodes: ['a'], removedLinks: ['link:a>b:call'] });
    expect(isStructuralDelta(base, shape)).toBe(true);
    expect(isStructuralDelta(base, delta({ nodes: [node('c', 'physical', 'C', null)] }))).toBe(true);
    expect(isStructuralDelta(base, delta({ links: [{ from: 'b', to: 'a', kind: 'call', label: 'Back', sourceRef: 'fixture' }] }))).toBe(true);
    const reshaped = applyStructureDelta(base, shape);
    expect(reshaped.nodes.map(item => item.id)).toEqual(['b', 'c']);
    expect(reshaped.links).toEqual([]);
  });

  it('lists the facts of a node as plain pairs and marks what only reports (AGT-3954)', () => {
    const find = (id: string): StructureNode => observedStructure.nodes.find(item => item.id === id)!;
    expect(isUndeclared(find('machine:review-1'))).toBe(true);
    expect(isUndeclared(find('machine:old-host'))).toBe(true);
    expect(isUndeclared(find('machine:spare'))).toBe(false);
    expect(factLines(find('machine:spare'))).toEqual([]);
    const host = Object.fromEntries(factLines(find('machine:review-1')).map(line => [line.label, line.value]));
    expect(host).toMatchObject({
      'Reported as': 'container · docker · review-host-1', Roles: 'review', CPU: '37% of 24 cores',
      Load: '2.50 · 2.00 · 1.50', Memory: '8.0 GiB of 32.0 GiB',
      'Disk /srv/work': '60.0 GiB used · 40.0 GiB free · runner work', Network: '↓ 1.2 KiB/s · ↑ 3.3 KiB/s',
      Slots: '1 busy · 1 free', 'Running cards': 'AGT-7', Version: '2.0 · stable 2.0 · current',
      Tools: 'claude 1.2.0 (behind, target 1.3.0), dotnet 10.0.100', Missing: 'toolchain:playwright',
      'Waiting cards': 'AGT-10 refused (toolchain:playwright)',
    });
    const studio = Object.fromEntries(factLines(find('machine:studio-server')).map(line => [line.label, line.value]));
    expect(studio).toMatchObject({
      'Workspace repository': '3.0 GiB · backup and git remote', 'Project checkouts': 'at least 9.0 GiB · git remote',
      'Application state directory': '200.0 MiB · backup', 'Secrets key': '0 KiB · not covered',
      'Edge certificates': 'not visible to the API',
    });
    expect(Object.fromEntries(factLines(find('machine:runner-host')).map(line => [line.label, line.value]))['Version'])
      .toBe('1.9 · stable 2.0 · behind · install pending');
  });
});
