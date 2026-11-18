import { describe, expect, it } from 'vitest';
import { applyStructureDelta, factRows, observationGlyph, observationWord, projectStructure, visibleBoxes,
  type StructureData, type StructureDelta, type StructureNode, type StructureObservation } from './structure.model';
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
    ['undeclared', 'up', 'Not declared', '○'],
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

  it('nests observed container hosts under the machine they report, with their runner roles', () => {
    const observed = (id: string, kind: string, label: string, parentId: string | null, role: string | null = null): StructureNode =>
      ({ ...node(id, kind, label, parentId, role), provenance: 'observed', observation: { difference: 'undeclared', health: 'up' } });
    const fixture: StructureData = { nodes: [node('installation', 'boundary', 'Installation', null),
      observed('machine:lab-vm', 'vm', 'lab-vm', 'installation'),
      ...[1, 2, 3].map(n => observed(`machine:review-${n}`, 'container', `review-${n}`, 'machine:lab-vm')),
      ...[1, 2, 3].map(n => observed(`runner:review-${n}`, 'process', 'Review runner', `machine:review-${n}`, 'review')),
      observed('machine:studio-server', 'vm', 'Studio server', 'installation'),
      observed('component:studio-server:api', 'container', 'Studio API', 'machine:studio-server', 'control')], links: [] };
    const boxes = visibleBoxes(projectStructure(fixture, 'host', 3, {}));
    const vm = boxes.find(box => box.id === 'machine:lab-vm')!;
    expect(vm.children.map(child => child.id)).toEqual(['machine:review-1', 'machine:review-2', 'machine:review-3']);
    expect(vm.summary).toEqual({ process: 3 });
    expect(boxes.find(box => box.id === 'machine:review-2')!.children.map(child => child.id)).toEqual(['runner:review-2']);
    expect(boxes.filter(box => box.observation?.difference === 'undeclared')).toHaveLength(9);
  });

  it('applies a delta only to the revision it was computed from', () => {
    const base: StructureData = { revision: 7, nodes: [node('installation', 'boundary', 'Installation', null),
      node('machine:a', 'host', 'a', 'installation'), node('machine:b', 'host', 'b', 'installation')],
      links: [{ id: 'link:machine:a|machine:b|call', from: 'machine:a', to: 'machine:b', kind: 'call', label: 'Route', sourceRef: 'fixture' }] };
    const delta: StructureDelta = { fromRevision: 7, revision: 8, generatedAt: '2026-11-17T12:00:00Z',
      upsertNodes: [{ ...node('machine:a', 'host', 'a', 'installation'), facts: { cpuPercent: 41 } }, node('machine:c', 'container', 'c', 'machine:a')],
      removeNodes: ['machine:b'], upsertLinks: [], removeLinks: ['link:machine:a|machine:b|call'] };
    const next = applyStructureDelta(base, delta)!;
    expect(next.revision).toBe(8);
    expect(next.nodes.map(item => item.id)).toEqual(['installation', 'machine:a', 'machine:c']);
    expect(next.nodes[1].facts?.cpuPercent).toBe(41);
    expect(next.links).toEqual([]);
    expect(applyStructureDelta({ ...base, revision: 6 }, delta)).toBeNull();
    expect(applyStructureDelta({ nodes: base.nodes, links: base.links }, delta)).toBeNull();
  });

  it('lists the facts a node carries and nothing it does not', () => {
    expect(factRows(null)).toEqual([]);
    const rows = factRows({ machine: { kind: 'container', containerRuntime: 'docker', containerName: 'review-1',
      virtualization: 'kvm', reportedParentId: 'lab-vm', source: 'host report' }, cpuCores: 24, cpuPercent: 12.4,
      load1: 1.5, load5: 1.25, load15: 1, memoryUsedBytes: 8 * 1024 ** 3, memoryTotalBytes: 32 * 1024 ** 3,
      mounts: [{ purpose: 'runner work', mount: '/', usedBytes: 84, freeBytes: 16, totalBytes: 100 },
        { purpose: 'runner work', mount: '/srv', usedBytes: 1, freeBytes: 1, totalBytes: 2 }],
      busySlots: 1, freeSlots: 2, runningCards: ['AGT-1'], version: '1.2', stableVersion: '1.3', releaseState: 'behind',
      tools: [{ key: 'toolchain:node', name: 'node', status: 'ready', version: '22.1.0' }],
      requirements: [{ key: 'toolchain:playwright', state: 'missing' }], heldCards: ['AGT-2'],
      dataLocations: [{ id: 'data:studio-server:workspace-repository', role: 'workspace-repository', label: 'Workspace repository',
        sizeBytes: 1024 ** 3, backupCovered: true, gitRemoteCovered: false }] });
    const value = (label: string) => rows.find(row => row.label === label)?.value;
    expect(value('Runs as')).toBe('Container · docker · review-1 · on kvm · in lab-vm');
    expect(value('CPU')).toBe('24 cores · 12% busy');
    expect(value('Load')).toBe('1.50 / 1.25 / 1.00');
    expect(value('Memory')).toBe('8.0 GiB of 32.0 GiB');
    expect(value('Disk runner work')).toBe('84% used · 0.0 GiB free');
    expect(value('Disk runner work 2')).toBeDefined();
    expect(value('Slots')).toBe('1 busy · 2 free');
    expect(value('Version')).toBe('1.2 · stable 1.3 · behind');
    expect(value('Requirements')).toBe('toolchain:playwright missing');
    expect(value('Workspace repository')).toBe('1.0 GiB · in backups · no Git remote');
    expect(value('Network')).toBeUndefined();
  });
});
