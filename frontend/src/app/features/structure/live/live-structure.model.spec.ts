import { describe, expect, it } from 'vitest';
import { canvasFacts, canvasGaps, containerScale, levelOf, nodeFacts, sourceGap } from './live-structure.facts';
import { liveSourcesFixture, liveStructureFixture, projectedCapacityFixture, sharedRunnerSourcesFixture, sharedRunnerStructureFixture } from './live-structure.fixtures';
import { layoutLiveScene } from './live-structure.layout';
import { NOT_OBSERVED, buildLiveScene, descendants, subtreeWarnings, type LiveNode, type LiveRect } from './live-structure.model';
import { NO_SOURCES } from './live-structure.sources';

const scene = () => buildLiveScene(liveStructureFixture, liveSourcesFixture);
const node = (id: string, from = scene()): LiveNode => from.byId.get(id)!;

describe('live structure scene (AGT-4024)', () => {
  it('names nodes by role and keeps reported host names for the card details', () => {
    const s = scene();
    expect(node('machine:review-3', s).name).toBe('Review host 3');
    expect(node('machine:review-3', s).technical).toBe('review-3');
    expect(node('machine:gate-1', s).name).toBe('Gate host');
    expect(node('machine:runner-host', s).name).toBe('Coding and review runner');
    expect(node('machine:linux-vm', s).name).toBe('Gate and review VM');
    expect(node('machine:home-lab', s).name).toBe('Gate and review machine');
    expect(node('machine:studio-server', s).name).toBe('Studio server');
    const names = s.order.map(item => item.name);
    for (const reported of ['review-1', 'gate-1', 'linux-vm', 'home-lab', 'runner-host', 'old-host']) expect(names).not.toContain(reported);
  });

  it('preserves zero traffic and queue samples and identifies expired API data', () => {
    const data = structuredClone(liveStructureFixture);
    const api = data.nodes.find(item => item.id === 'component:studio-server:api')!;
    const sample = { observedAt: '2026-11-17T11:55:00Z', freshUntil: '2026-11-17T11:56:00Z', observedBy: 'API' };
    api.facts = { ...api.facts, api: { sample, requestsInFlight: 0, requestsPerSecond: 0,
      queues: { queuedTasks: 0, review: null, coding: null, integration: null, postMerge: null, gate: null } } };
    data.generatedAt = '2026-11-17T12:00:00Z';
    const scene = buildLiveScene(data, NO_SOURCES);
    const facts = nodeFacts(scene.byId.get(api.id)!, scene);
    expect(scene.queued).toBe(0);
    expect(facts.find(fact => fact.key === 'requests')?.values).toContain('0.0 req/s');
    expect(facts.find(fact => fact.key === 'api-age')?.values).toContain('5 min ago');
  });

  it('nests machine, virtual machine, container and the services in it', () => {
    const s = scene();
    expect(node('machine:home-lab', s).kind).toBe('machine');
    expect(node('machine:linux-vm', s).kind).toBe('vm');
    expect(node('machine:review-1', s).kind).toBe('container');
    expect(node('machine:review-1', s).parent?.id).toBe('machine:linux-vm');
    expect(node('component:review-1:review-1-runner', s).kind).toBe('service');
    expect(node('machine:runner-host', s).kind).toBe('service');
    expect(node('external:cdn', s).parent).toBeNull();
    expect(s.outside.map(item => item.id)).toContain('backup-target');
  });

  it('derives warnings from reported facts and totals from execution capacity', () => {
    const s = scene();
    expect(node('machine:review-3', s).warnings.map(w => w.title)).toEqual(['Missing toolchain:playwright', 'AGT-10 refused']);
    expect(node('machine:studio-server#storage', s).warnings.map(w => w.title))
      .toEqual(['Disk 84% full', 'Backups not configured', '2 data locations have no copy']);
    expect(node('observed-container:runner-host:deploy-stage-api-1', s).warnings[0].tone).toBe('danger');
    expect(s.warnings.length).toBe(s.order.reduce((sum, item) => sum + item.warnings.length, 0));
    expect(subtreeWarnings(s.boundary).length).toBe(s.warnings.length - s.outside.reduce((sum, item) => sum + item.warnings.length, 0));
    expect(s.totals.busySlots).toBe(5);
    expect(s.totals.totalSlots).toBe(12);
    expect(s.boundary.health.word).toBe(`${s.warnings.length} open warnings`);
  });

  it('reads backup state and coverage, and says not observed when the status cannot be read', () => {
    const s = scene();
    expect(s.backup.health.word).toBe('Backups not configured');
    expect([s.backup.covered, s.backup.locations]).toEqual([0, 4]);
    expect(s.backup.target?.health.word).toBe('Not configured');
    expect(s.links.find(link => link.layer === 'backup')?.broken).toBe(true);
    const blind = buildLiveScene(liveStructureFixture, NO_SOURCES);
    expect(blind.backup.observed).toBe(false);
    expect(blind.backup.target?.health.word).toBe('Not observed');
    expect(blind.order.flatMap(item => item.warnings).some(w => w.title.startsWith('Backups'))).toBe(false);
  });

  it('never invents a value: what no source reports is a not observed fact', () => {
    const s = scene();
    const api = nodeFacts(node('component:studio-server:api', s), s);
    for (const key of ['cpu', 'memory', 'ports', 'deploy', 'uptime', 'restarts', 'image', 'log'])
      expect(api.find(fact => fact.key === key), key).toMatchObject({ values: [NOT_OBSERVED], observed: false });
    expect(api.find(fact => fact.key === 'version')?.values.at(-1)).toBe('2.0');
    const review = nodeFacts(node('machine:review-2', s), s);
    expect(review.find(fact => fact.key === 'image')?.values[0]).toBe('registry.example/studio/review-host:2.0');
    expect(review.find(fact => fact.key === 'restarts')?.values[0]).toBe('1 of the review daemon');
    expect(review.find(fact => fact.key === 'job')?.values[0]).toBe('AGT-8, AGT-9');
    expect(review.find(fact => fact.key === 'ports')?.observed).toBe(false);
    // A missing value never shows before the container level.
    for (const fact of [...api, ...review]) if (!fact.observed) expect(fact.level).toBeGreaterThanOrEqual(3);
    const blind = buildLiveScene(liveStructureFixture, NO_SOURCES);
    expect(nodeFacts(node('machine:review-2', blind), blind).find(fact => fact.key === 'uptime')?.observed).toBe(false);
  });

  it('gives every node a state, and every missing value the reason it is missing', () => {
    for (const s of [scene(), buildLiveScene(liveStructureFixture, NO_SOURCES)]) {
      for (const item of s.order) {
        if (item.kind === 'boundary') continue;
        const facts = nodeFacts(item, s);
        // The state is a fact of no layer, so no combination of layers can leave a node without one.
        expect(facts.find(fact => fact.key === 'status' && fact.layer === null), item.id).toBeTruthy();
        for (const fact of facts) if (!fact.observed) {
          expect(fact.why, `${item.id} ${fact.key}`).toMatch(/\.$/);
          expect(fact.gap, `${item.id} ${fact.key}`).toBeTruthy();
        }
      }
    }
    const s = scene();
    const fact = (id: string, key: string) => nodeFacts(node(id, s), s).find(item => item.key === key);
    expect(fact('machine:review-3', 'status')?.values[0]).toBe('up · seen 0 s ago');
    expect(fact('component:studio-server:api', 'cpu')?.why).toBe('No agent runs inside it; load and disk are reported for its host only.');
    expect(fact('component:studio-server:api', 'deploy')?.why).toBe('Deploys are recorded per project environment, not per node.');
    expect(fact('machine:spare', 'status')).toMatchObject({ observed: false, why: 'It is declared in the placement file and no host reports from it.' });
    expect(fact('machine:home-lab', 'status')?.why).toBe('A host inside names this machine; nothing reports from the machine itself.');
    expect(sourceGap(node('external:cdn', s)).short).toBe('outside, not probed');
    expect(sourceGap(node('component:review-1:review-1-runner', s)).short).toBe('reported by name only');
  });
});

describe('live structure unique execution capacity (AGT-4203)', () => {
  const projected = () => buildLiveScene(projectedCapacityFixture, NO_SOURCES);

  it('counts one runner once when both the host and its process report 2 of 4 slots', () => {
    const data = { ...projectedCapacityFixture, nodes: projectedCapacityFixture.nodes.filter(item =>
      item.id === 'installation' || item.facts?.hostId === 'coding-host') };
    const s = buildLiveScene(data, NO_SOURCES);
    expect(s.totals).toMatchObject({ busySlots: 2, totalSlots: 4, reporting: 1 });
    for (const id of ['machine:coding-host', 'component:coding-host:coding-runner']) {
      expect(node(id, s).slots).toEqual({ busy: 2, total: 4, cards: ['AGT-1', 'AGT-2'] });
      expect(nodeFacts(node(id, s), s).find(fact => fact.key === 'slots')?.values[0]).toBe('2 of 4 in use');
    }
  });

  it('counts both roles once and one reporting identity on their shared host', () => {
    const s = projected();
    const processes = s.order.filter(item => item.source?.kind === 'process');
    expect(processes.map(item => item.source?.unitId).sort()).toEqual(['coding-runner', 'shared-coding', 'shared-review']);
    expect(new Set(processes.map(item => item.source?.unitId)).size).toBe(3);
    expect(s.totals).toMatchObject({ busySlots: 5, totalSlots: 10, reporting: 2 });
    expect(s.totals.busySlots).toBe(processes.reduce((sum, item) => sum + item.slots!.busy, 0));
    expect(s.totals.totalSlots).toBe(processes.reduce((sum, item) => sum + item.slots!.total!, 0));
    expect(node('machine:shared-host', s).slots).toEqual({ busy: 3, total: 6, cards: ['AGT-3', 'AGT-4', 'AGT-5'] });
    expect(node('component:shared-host:shared-coding', s).slots?.total).toBe(4);
    expect(node('component:shared-host:shared-review', s).slots?.total).toBe(2);
    const rootFacts = nodeFacts(s.boundary, s);
    expect(rootFacts.find(fact => fact.key === 'slots')?.values[0]).toBe('5 of 10');
    expect(rootFacts.find(fact => fact.key === 'reporting')?.values[0]).toBe('2');
  });

  it('keeps capacity from a different reporting host nested below an aggregate', () => {
    const data = { ...projectedCapacityFixture, nodes: projectedCapacityFixture.nodes.map(item =>
      item.id === 'machine:shared-host' ? { ...item, parentId: 'machine:coding-host' } : item).reverse() };
    expect(buildLiveScene(data, NO_SOURCES).totals).toMatchObject({ busySlots: 5, totalSlots: 10, reporting: 2 });
  });

  it('uses host identity even when a declared process is placed outside its host aggregate', () => {
    const data = { ...projectedCapacityFixture, nodes: projectedCapacityFixture.nodes.map(item =>
      item.unitId === 'shared-review' ? { ...item, parentId: 'installation', provenance: 'declared' as const } : item) };
    expect(buildLiveScene(data, NO_SOURCES).totals).toMatchObject({ busySlots: 5, totalSlots: 10, reporting: 2 });
  });

  it('uses process capacity when no host aggregate is available', () => {
    const data = { ...projectedCapacityFixture, nodes: projectedCapacityFixture.nodes.filter(item => !item.id.startsWith('machine:')) };
    expect(buildLiveScene(data, NO_SOURCES).totals).toMatchObject({ busySlots: 5, totalSlots: 10, reporting: 2 });
  });

  it('counts only distinct host identities with an Up report', () => {
    const data = { ...projectedCapacityFixture, nodes: projectedCapacityFixture.nodes.map(item =>
      item.facts?.hostId === 'shared-host' ? { ...item, observation: { difference: 'stale' as const, health: 'stale' } } : item) };
    expect(buildLiveScene(data, NO_SOURCES).totals.reporting).toBe(1);
  });
});

describe('live structure runner identity (AGT-4219)', () => {
  const factsOf = (s: ReturnType<typeof buildLiveScene>, id: string) => nodeFacts(node(id, s), s);
  const processId = (role: string) => `component:shared-host:shared-${role}`;

  for (const reverse of [false, true]) {
    it(`keeps each process image, restarts, uptime and events with its runner (reversed: ${reverse})`, () => {
      const runners = [...sharedRunnerSourcesFixture.runners!];
      const s = buildLiveScene(sharedRunnerStructureFixture, { ...sharedRunnerSourcesFixture, runners: reverse ? runners.reverse() : runners });
      for (const [role, version, restarts, uptime] of [['coding', '2.1', 0, '4 h'], ['review', '2.2', 2, '60 min']] as const) {
        const id = processId(role), facts = factsOf(s, id);
        const fact = (key: string) => facts.find(item => item.key === key);
        expect(node(id, s).runner?.runnerId).toBe(`shared-${role}`);
        expect(fact('image')?.values[0]).toBe(`registry.example/studio/${role}-host:${version}`);
        expect(fact('version')?.values.at(-1)).toBe(version);
        expect(fact('restarts')?.values[0]).toBe(`${restarts} of the review daemon`);
        expect(fact('uptime')?.values.at(-1)).toBe(uptime);
        expect(facts.filter(item => item.key.startsWith('event:'))).toHaveLength(restarts);
        if (role === 'review') expect(fact('event:0')?.values[0]).toContain('3 reviews lost');
      }
    });
  }

  it('distinguishes both roles on the host without selecting a representative runner', () => {
    const s = buildLiveScene(sharedRunnerStructureFixture, sharedRunnerSourcesFixture);
    const facts = factsOf(s, 'machine:shared-host');
    expect(node('machine:shared-host', s).runner).toBeNull();
    expect(facts.find(item => item.key === 'runners')?.values[0]).toBe('coding (shared-coding) · review (shared-review)');
    for (const [role, version, count] of [['coding', '2.1', 0], ['review', '2.2', 2]] as const) {
      const image = facts.find(item => item.key === `runner:shared-${role}:image`);
      expect(image?.label).toBe(`${role} (shared-${role}) image`);
      expect(image?.values[0]).toBe(`registry.example/studio/${role}-host:${version}`);
      expect(facts.find(item => item.key === `runner:shared-${role}:restarts`)?.values[0]).toBe(`${count} of the review daemon`);
      expect(facts.find(item => item.key === `runner:shared-${role}:version`)?.values.at(-1)).toBe(version);
      expect(facts.find(item => item.key === `runner:shared-${role}:uptime`)?.values.at(-1)).toBe(role === 'coding' ? '4 h' : '60 min');
      expect(facts.filter(item => item.key.startsWith(`runner:shared-${role}:event:`))).toHaveLength(count);
    }
    expect(facts.some(item => ['image', 'version', 'restarts', 'uptime'].includes(item.key))).toBe(false);
    const reversed = buildLiveScene(sharedRunnerStructureFixture, { ...sharedRunnerSourcesFixture,
      runners: [...sharedRunnerSourcesFixture.runners!].reverse() });
    for (const id of ['machine:shared-host', processId('coding'), processId('review')])
      expect(factsOf(reversed, id)).toEqual(factsOf(s, id));
  });

  it('distinguishes runner facts on a host projected as a virtual machine', () => {
    const data = { ...sharedRunnerStructureFixture, nodes: sharedRunnerStructureFixture.nodes.map(item =>
      item.id === 'machine:shared-host' ? { ...item, kind: 'vm' } : item) };
    const s = buildLiveScene(data, sharedRunnerSourcesFixture);
    expect(node('machine:shared-host', s).kind).toBe('vm');
    expect(factsOf(s, 'machine:shared-host').find(item => item.key === 'runners')?.observed).toBe(true);
  });

  it('uses its own registration time when a runner has no restart timestamp', () => {
    const runners = sharedRunnerSourcesFixture.runners!.map(runner => ({ ...runner, restartedAt: null }));
    const s = buildLiveScene(sharedRunnerStructureFixture, { ...sharedRunnerSourcesFixture, runners });
    expect(factsOf(s, processId('review')).find(item => item.key === 'uptime')?.values.at(-1)).toBe('15 d');
  });

  it('does not fall back to a host peer when the declared runner is absent', () => {
    const s = buildLiveScene(sharedRunnerStructureFixture, { ...sharedRunnerSourcesFixture,
      runners: [sharedRunnerSourcesFixture.runners![0]] });
    expect(node(processId('review'), s).runner).toBeNull();
    const facts = factsOf(s, processId('review'));
    expect(facts.find(item => item.key === 'uptime')?.observed).toBe(false);
    expect(facts.filter(item => item.key.startsWith('event:'))).toEqual([]);
    expect(node('machine:shared-host', s).runner?.runnerId).toBe('shared-coding');
  });

  it('joins declared runner identities case-insensitively even without host facts', () => {
    const data = { ...sharedRunnerStructureFixture, nodes: sharedRunnerStructureFixture.nodes.map(item =>
      item.unitId === 'shared-review' ? { ...item, unitId: 'SHARED-REVIEW', facts: { ...item.facts, hostId: null } } : item) };
    const s = buildLiveScene(data, sharedRunnerSourcesFixture);
    expect(node(processId('review'), s).runner?.runnerId).toBe('shared-review');
  });
});

describe('live structure semantic zoom (AGT-4024)', () => {
  it('adds facts level by level and never drops one when zooming in', () => {
    const s = scene(), host = node('machine:review-1', s);
    expect(canvasFacts(host, s, 1, ['perf'])).toEqual([]);
    const keys = (level: 2 | 3 | 4) => canvasFacts(host, s, level, ['warnings', 'network', 'perf']).map(fact => fact.key);
    expect(keys(2)).toEqual(expect.arrayContaining(['cpu', 'memory', 'slots', 'links']));
    expect(keys(2)).not.toContain('image');
    expect(keys(3)).toEqual(expect.arrayContaining(['status', 'image', 'version', 'restarts', 'uptime', 'job', 'load']));
    expect(keys(3)).not.toContain('seen');
    expect(keys(4)).toEqual(expect.arrayContaining(['traffic', 'seen', 'sampled']));
    for (const key of keys(2)) expect(keys(3)).toContain(key);
    for (const key of keys(3)) expect(keys(4)).toContain(key);
    // Only values somebody reports are rows.
    expect(canvasFacts(host, s, 4, ['warnings', 'network', 'perf']).every(fact => fact.observed)).toBe(true);
  });

  it('says what is not observed and why from the container level on, grouped by reason', () => {
    const s = scene(), host = node('machine:review-1', s), api = node('component:studio-server:api', s);
    expect(canvasGaps(host, s, 2, ['perf'])).toEqual([]);
    const near = canvasGaps(host, s, 3, []);
    expect(near.map(gap => gap.kind)).toEqual(['collect']);
    expect(near[0].labels).toEqual(['ports', 'last deploy']);
    expect(near[0].values[0]).toBe('Not observed: ports, last deploy. The Studio does not collect this per node yet.');
    expect(near[0].values.at(-1)).toBe('Not observed: not collected per node yet.');
    expect(canvasGaps(host, s, 4, [])[0].labels).toEqual(['ports', 'last deploy', 'recent events', 'log lines']);
    // A container nobody reports from inside: its load is missing for another reason than its ports.
    const gaps = canvasGaps(api, s, 3, ['perf']);
    expect(gaps.map(gap => gap.kind).sort()).toEqual(['collect', 'source']);
    expect(gaps.find(gap => gap.kind === 'source')).toMatchObject({ labels: ['cpu', 'memory'],
      values: expect.arrayContaining(['Not observed: cpu, memory. No agent runs inside it; load and disk are reported for its host only.']) });
    // Facts of a layer that is off are not missed on the canvas either.
    expect(canvasGaps(api, s, 3, []).flatMap(gap => gap.labels)).not.toContain('cpu');
    // Each wording is shorter than the one before, so a smaller node still says the reason.
    for (const gap of [...near, ...gaps]) for (let i = 1; i < gap.values.length; i++) expect(gap.values[i].length).toBeLessThan(gap.values[i - 1].length);
  });

  it('leaves no readable box empty at the machine level: a state where one is reported, else the reason', () => {
    const s = scene();
    const web = node('component:studio-server:web', s), spare = node('machine:spare', s);
    expect(canvasFacts(web, s, 2, []).map(fact => fact.key)).toEqual(['status']);
    expect(canvasGaps(web, s, 2, [])).toEqual([]);
    expect(canvasFacts(spare, s, 2, [])).toEqual([]);
    expect(canvasGaps(spare, s, 2, []).map(gap => gap.values.at(-1))).toEqual(['Not observed: declared, no host reports from it.']);
    expect(canvasGaps(spare, s, 1, [])).toEqual([]);
  });

  it('shows only the facts of active layers, the layer switched on last first', () => {
    const s = scene(), storage = node('machine:studio-server#storage', s);
    const off = canvasFacts(storage, s, 4, []).map(fact => fact.layer);
    expect(off.every(layer => layer === null)).toBe(true);
    const backupFirst = canvasFacts(storage, s, 3, ['backup', 'storage']);
    const level3 = backupFirst.filter(fact => fact.level === 3).map(fact => fact.layer);
    expect(level3.indexOf('backup')).toBeLessThan(level3.indexOf('storage'));
    expect(level3.lastIndexOf('backup')).toBeLessThan(level3.indexOf('storage'));
    const storageFirst = canvasFacts(storage, s, 3, ['storage', 'backup']).filter(fact => fact.level === 3).map(fact => fact.layer);
    expect(storageFirst.indexOf('storage')).toBeLessThan(storageFirst.indexOf('backup'));
    expect(backupFirst.find(fact => fact.key === 'coverage')?.values[0]).toBe('0 of 4 data locations');
  });

  it('judges the level from the size a node has on screen', () => {
    const s = scene();
    layoutLiveScene(s, 'wide', 1.6);
    const unit = node('machine:review-1', s), machine = node('machine:studio-server', s);
    expect([0.4, 0.8, 1.3, 2.1].map(k => levelOf(unit, unit.rect.w * k, unit.rect.h * k, k))).toEqual([1, 2, 3, 4]);
    expect([0.4, 1, 1.7, 2.6].map(k => levelOf(machine, machine.rect.w * k, machine.rect.h * k, k))).toEqual([1, 2, 3, 4]);
  });

  it('puts everything in view at the container level once a container is: its services, and the small boxes beside it', () => {
    const s = scene();
    layoutLiveScene(s, 'wide', 1.6);
    const unit = node('machine:review-1', s), chip = node('component:review-1:review-1-runner', s);
    for (const k of [0.4, 0.8, 1.3, 2.1, 3.5]) expect(levelOf(chip, chip.rect.w * k, chip.rect.h * k, k), `chip at ${k}`).toBe(levelOf(unit, unit.rect.w * k, unit.rect.h * k, k));
    for (const id of ['machine:spare', 'external:cdn', 'backup-target']) {
      const small = node(id, s);
      expect([0.4, 0.8, 1.3, 2.1].map(k => levelOf(small, small.rect.w * k, small.rect.h * k, k)), id).toEqual([1, 2, 3, 4]);
    }
    // The scale a jump lands on shows a node at the container level, not yet at the closest.
    for (const item of s.order) {
      if (item.kind === 'boundary' || (item.children.length && item.kind !== 'container' && item.kind !== 'service')) continue;
      const k = containerScale(item);
      expect(levelOf(item, item.rect.w * k, item.rect.h * k, k), item.id).toBe(3);
    }
  });
});

describe('live structure layout (AGT-4024)', () => {
  const overlaps = (a: LiveRect, b: LiveRect): boolean => a.x < b.x + b.w - 0.5 && b.x < a.x + a.w - 0.5 && a.y < b.y + b.h - 0.5 && b.y < a.y + a.h - 0.5;
  const inside = (inner: LiveRect, outer: LiveRect): boolean => inner.x >= outer.x && inner.y >= outer.y
    && inner.x + inner.w <= outer.x + outer.w + 0.5 && inner.y + inner.h <= outer.y + outer.h + 0.5;

  for (const [mode, aspect] of [['wide', 1.6], ['wide', 1.1], ['tall', 0.6]] as const) {
    it(`places every node inside its parent and beside its siblings (${mode}, aspect ${aspect})`, () => {
      const s = scene(), layout = layoutLiveScene(s, mode, aspect);
      for (const item of s.order) {
        expect(item.rect.w, item.id).toBeGreaterThan(0);
        expect(item.rect.h, item.id).toBeGreaterThan(0);
        expect(inside(item.rect, layout.bounds), item.id).toBe(true);
        if (item.parent) expect(inside(item.rect, item.parent.rect), item.id).toBe(true);
        for (let i = 0; i < item.children.length; i++) for (let j = i + 1; j < item.children.length; j++)
          expect(overlaps(item.children[i].rect, item.children[j].rect), `${item.children[i].id} over ${item.children[j].id}`).toBe(false);
      }
      for (const outside of s.outside) expect(overlaps(outside.rect, s.boundary.rect), outside.id).toBe(false);
    });
  }

  it('routes every link at right angles through free space, not across a container, service or storage strip', () => {
    const s = scene(), layout = layoutLiveScene(s, 'wide', 1.6);
    expect(layout.routes.length).toBe(s.links.length);
    const blocks = s.order.filter(item => item.kind === 'container' || item.kind === 'service' || item.kind === 'disk');
    for (const route of layout.routes) {
      const ends = new Set([route.link.a, route.link.b, ...descendants(route.link.a), ...descendants(route.link.b),
        route.link.a.parent, route.link.b.parent]);
      for (let i = 1; i < route.points.length; i++) {
        const [a, b] = [route.points[i - 1], route.points[i]];
        expect(a[0] === b[0] || a[1] === b[1], `${route.link.id} segment ${i}`).toBe(true);
        const segment = { x: Math.min(a[0], b[0]), y: Math.min(a[1], b[1]), w: Math.abs(a[0] - b[0]) || 1, h: Math.abs(a[1] - b[1]) || 1 };
        for (const block of blocks) if (!ends.has(block)) expect(overlaps(segment, block.rect), `${route.link.id} crosses ${block.name}`).toBe(false);
      }
    }
  });

  it('takes the arrangement that shows the installation largest in the pane', () => {
    const fit = (aspect: number): number => { const bounds = layoutLiveScene(scene(), 'wide', aspect).bounds; return Math.min(aspect / bounds.w, 1 / bounds.h); };
    const wideBounds = layoutLiveScene(scene(), 'wide', 2.4).bounds, squareBounds = layoutLiveScene(scene(), 'wide', 0.9).bounds;
    expect(wideBounds.w / wideBounds.h).toBeGreaterThan(squareBounds.w / squareBounds.h);
    expect(fit(2.4)).toBeGreaterThan(0);
  });
});
