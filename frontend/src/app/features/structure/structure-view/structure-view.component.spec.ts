import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import { observedStructure } from '../structure.fixtures';
import type { StructureData, StructureDelta } from '../structure.model';
import { StructureViewComponent } from './structure-view.component';

const isStructureRead = (url: string): boolean => url === '/api/v1/operations/structure';

function setup(connected = true) {
  const handlers = new Set<(delta: StructureDelta) => void>();
  const hub = {
    connected: signal(connected),
    onStructureDelta: (handler: (delta: StructureDelta) => void) => { handlers.add(handler); return () => handlers.delete(handler); },
    subscribeStructure: vi.fn(async (scope: string, environment: string | null) => ({ scope, environment, revision: 40 })),
    unsubscribeStructure: vi.fn(),
  };
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
      { provide: JobsHubClient, useValue: hub }],
  });
  const fixture = TestBed.createComponent(StructureViewComponent);
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/v1/operations/structure/library').flush({ registries: [], consumers: [] });
  const settle = async (): Promise<void> => {
    await new Promise(resolve => setTimeout(resolve, 0));
    fixture.detectChanges();
    await fixture.whenStable();
  };
  const push = (delta: StructureDelta): void => { for (const handler of handlers) handler(delta); };
  return { fixture, http, hub, settle, push, root: fixture.nativeElement as HTMLElement };
}

const delta = (data: StructureData, change: Partial<StructureDelta>): StructureDelta => ({
  scope: 'workspace', environment: null, baseRevision: data.revision!, revision: data.revision! + 1,
  generatedAt: '2026-11-17T12:00:05Z', nodes: [], removedNodes: [], links: [], removedLinks: [], ...change,
});

describe('StructureViewComponent with observed hosts (AGT-3954)', () => {
  afterEach(() => { localStorage.clear(); TestBed.resetTestingModule(); });

  it('shows hosts that only report, nested, marked as not declared, with their facts', async () => {
    const { fixture, http, settle, root } = setup();
    fixture.detectChanges();
    http.expectOne(request => isStructureRead(request.url)).flush(observedStructure);
    fixture.componentInstance.setLevel(3);
    await settle();

    const box = (id: string): HTMLElement => root.querySelector<HTMLElement>(`[data-node-id="${id}"]`)!;
    expect(box('machine:review-1').textContent).toContain('review-1');
    expect(box('machine:review-1').textContent).toContain('not declared');
    // An undeclared host that reports is up, not "not declared" instead of a health.
    expect(box('machine:review-1').querySelector('.structure-observation-state')!.textContent).toContain('Up');
    expect(box('machine:review-1').getAttribute('aria-label')).toContain('observed, not declared');
    expect(box('machine:spare').textContent).toContain('Not observed');
    expect(box('machine:spare').textContent).not.toContain('not declared');
    // Container in virtual machine in physical machine, from the reports alone.
    const boxes = fixture.componentInstance.layout().boxes;
    const within = (inner: string, outer: string): boolean => {
      const a = boxes.find(item => item.id === inner)!, b = boxes.find(item => item.id === outer)!;
      return a.x >= b.x && a.y >= b.y && a.x + a.w <= b.x + b.w && a.y + a.h <= b.y + b.h;
    };
    expect(within('machine:review-1', 'machine:linux-vm')).toBe(true);
    expect(within('machine:linux-vm', 'machine:home-lab')).toBe(true);
    expect(within('component:studio-server:api', 'machine:studio-server')).toBe(true);
    expect(root.querySelector('[data-testid="structure-link-key"]')!.textContent).toContain('4.5 ms');

    fixture.componentInstance.selected.set('machine:studio-server');
    await settle();
    const facts = root.querySelector('[data-testid="structure-facts"]')!.textContent!;
    expect(facts).toContain('Observed, not declared');
    expect(facts).toContain('12% of 12 cores');
    expect(facts).toContain('84.0 GiB used · 16.0 GiB free');
    expect(facts).toContain('Workspace repository');
    expect(facts).toContain('backup and git remote');
    expect(facts).toContain('not visible to the API');
    fixture.componentInstance.selected.set('machine:review-1');
    await settle();
    const host = root.querySelector('[data-testid="structure-facts"]')!.textContent!;
    expect(host).toContain('1 busy · 1 free');
    expect(host).toContain('AGT-7');
    expect(host).toContain('claude 1.2.0 (behind, target 1.3.0)');
    expect(host).toContain('AGT-10 refused (toolchain:playwright)');
    fixture.destroy();
  });

  it('patches fact updates from the hub in place and reads again only for a gap or a changed shape', async () => {
    const { fixture, http, hub, settle, push, root } = setup();
    fixture.detectChanges();
    http.expectOne(request => isStructureRead(request.url)).flush(observedStructure);
    fixture.componentInstance.setLevel(3);
    fixture.componentInstance.setOverlay('load');
    await settle();
    expect(hub.subscribeStructure).toHaveBeenCalledWith('workspace', null);
    expect(root.querySelector('[data-testid="structure-view"]')!.getAttribute('data-live')).toBe('true');
    expect(root.querySelector('[data-node-id="machine:review-1"]')!.textContent).toContain('37% CPU');

    const review = observedStructure.nodes.find(node => node.id === 'machine:review-1')!;
    push(delta(observedStructure, { nodes: [{ ...review, observation: { ...review.observation!, cpuPercent: 81 } }] }));
    await settle();
    expect(root.querySelector('[data-node-id="machine:review-1"]')!.textContent).toContain('81% CPU');
    expect(root.querySelector('[data-testid="structure-view"]')!.getAttribute('data-revision')).toBe('41');
    http.expectNone(request => isStructureRead(request.url));

    // A delta of another scope is ignored; an older one is dropped.
    push({ ...delta(observedStructure, { nodes: [{ ...review, label: 'other scope' }] }), scope: 'project:PROJ-001', baseRevision: 41, revision: 42 });
    push(delta(observedStructure, { nodes: [{ ...review, label: 'stale delta' }] }));
    await settle();
    expect(root.textContent).not.toContain('other scope');
    expect(root.textContent).not.toContain('stale delta');
    http.expectNone(request => isStructureRead(request.url));

    // A host that disappears changes the shape: the view reads the whole structure with its entity tag.
    push({ ...delta(observedStructure, { removedNodes: ['machine:old-host'] }), baseRevision: 41, revision: 42 });
    await settle();
    const read = http.expectOne(request => isStructureRead(request.url));
    expect(read.request.headers.get('If-None-Match')).toBe('"41"');
    read.flush({ ...observedStructure, revision: 42, nodes: observedStructure.nodes.filter(node => node.id !== 'machine:old-host') });
    await settle();
    expect(root.querySelector('[data-node-id="machine:old-host"]')).toBeNull();

    // A gap in the revisions is read as well, and 304 keeps the nodes.
    push({ ...delta(observedStructure, { nodes: [review] }), baseRevision: 50, revision: 51 });
    await settle();
    http.expectOne(request => isStructureRead(request.url)).flush(null, { status: 304, statusText: 'Not Modified' });
    await settle();
    expect(root.querySelector('[role="alert"]')).toBeNull();
    expect(root.querySelector('[data-node-id="machine:review-1"]')).not.toBeNull();
    fixture.destroy();
    expect(hub.unsubscribeStructure).toHaveBeenCalledWith('workspace', null);
  });

  it('keeps reading on its own while the hub is down', async () => {
    const { fixture, http, hub, settle, root } = setup(false);
    fixture.detectChanges();
    http.expectOne(request => isStructureRead(request.url)).flush(observedStructure);
    await settle();
    expect(hub.subscribeStructure).not.toHaveBeenCalled();
    expect(root.querySelector('[data-testid="structure-view"]')!.getAttribute('data-live')).toBe('false');
    hub.connected.set(true);
    await settle();
    expect(hub.subscribeStructure).toHaveBeenCalledWith('workspace', null);
    expect(root.querySelector('[data-testid="structure-view"]')!.getAttribute('data-live')).toBe('true');
    fixture.destroy();
  });
});
