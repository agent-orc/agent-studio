import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import {
  RuntimeCapacityEditorComponent,
  type HostProjectPolicyChange,
  type RuntimeCapacityChange,
} from './runtime-capacity-editor';
import type { RemoteHost } from '../../models/remote-host.model';

const HOST: RemoteHost = {
  id: 'runner-a',
  name: 'Runner A',
  role: 'remote',
  address: null,
  clientId: 'runner-a',
  status: 'online',
  os: 'Linux',
  lastHeartbeatAt: new Date().toISOString(),
  uptimeLabel: null,
  capabilities: [],
  cliQuotas: [],
  stats: null,
  activeTaskCount: 2,
  effectiveMaxParallelism: 4,
  runtimeCapacityAppliedAt: new Date().toISOString(),
  runtimeCapacityAppliedVersion: 1,
  capacityHostId: 'host-a',
  projectPolicy: {
    hostId: 'host-a',
    allowAllProjects: false,
    allowedProjectIds: ['PROJ-001'],
    version: 2,
    updatedAt: new Date().toISOString(),
  },
  runtimeCapacity: {
    hostId: 'host-a',
    maxParallelism: 4,
    targetLoadPercent: 80,
    rampStrategy: 'balanced',
    version: 1,
    updatedAt: new Date().toISOString(),
  },
};

describe('RuntimeCapacityEditorComponent', () => {
  it('shows light chat separately and a heavy chat as borrowed coding capacity', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', { ...HOST, runtimeCapacity: {
      ...HOST.runtimeCapacity!, maxParallelism: 5,
    } });
    fixture.componentRef.setInput('boardActiveSlots', 4);
    fixture.componentRef.setInput('chatUsage', [{
      hostName: 'Runner A', projectName: 'Agent Studio', activeTurns: 1,
      heavyTurns: 0, cpuPercent: 12, tokens: 100, costUsd: 0.01,
    }]);
    fixture.detectChanges();
    expect(fixture.componentInstance.freeSlots()).toBe(1);
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-chat-summary"]')?.textContent)
      .toContain('1 active chat turn,');

    fixture.componentRef.setInput('chatUsage', [{
      hostName: 'Runner A', projectName: 'Agent Studio', activeTurns: 1,
      heavyTurns: 1, cpuPercent: 82, tokens: 100, costUsd: 0.01,
    }]);
    fixture.detectChanges();
    expect(fixture.componentInstance.freeSlots()).toBe(0);
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-slots"]')?.textContent)
      .toContain('5 slots, 4 coding, 1 taken by a heavy chat turn');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-chat-usage"]')?.textContent)
      .toContain('CPU 82%');
    fixture.componentRef.setInput('boardActiveSlots', 5);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-slots"]')?.textContent)
      .toContain('5 coding, 1 heavy chat turn running alongside, 0 free');
    fixture.destroy();
  });

  it('shows the central total and emits a validated capacity update', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', HOST);
    fixture.componentRef.setInput('boardActiveSlots', 2);
    let change: RuntimeCapacityChange | null = null;
    fixture.componentInstance.capacityChange.subscribe(value => { change = value; });
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('[data-testid="remote-host-slots"]')?.textContent)
      .toContain('2 active / 2 free / 4 total');
    (element.querySelector('[data-testid="remote-host-capacity-input"]') as HTMLInputElement).value = '6';
    (element.querySelector('[data-testid="remote-host-capacity-input"]') as HTMLInputElement)
      .dispatchEvent(new Event('input'));
    (element.querySelector('[data-testid="remote-host-target-load-input"]') as HTMLInputElement).value = '85';
    (element.querySelector('[data-testid="remote-host-target-load-input"]') as HTMLInputElement)
      .dispatchEvent(new Event('input'));
    (element.querySelector('[data-testid="remote-host-ramp-select"]') as HTMLSelectElement).value = 'aggressive';
    (element.querySelector('[data-testid="remote-host-ramp-select"]') as HTMLSelectElement)
      .dispatchEvent(new Event('change'));
    (element.querySelector('[data-testid="remote-host-capacity-save"]') as HTMLButtonElement).click();

    expect(change).toEqual({
      id: 'runner-a',
      maxParallelism: 6,
      targetLoadPercent: 85,
      rampStrategy: 'aggressive',
    });
  });

  it('keeps the ceiling as the slot total while the active count breathes', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', { ...HOST, activeTaskCount: 7, availableSlots: 1 });
    fixture.componentRef.setInput('boardActiveSlots', 7);
    fixture.detectChanges();
    const slots = () => fixture.nativeElement
      .querySelector('[data-testid="remote-host-slots"]')?.textContent;

    // The daemon's own "1 free" must not turn the total into active + 1.
    expect(slots()).toContain('7 active / 0 free / 4 total');

    fixture.componentRef.setInput('host', { ...HOST, activeTaskCount: 2, availableSlots: 1 });
    fixture.componentRef.setInput('boardActiveSlots', 2);
    fixture.detectChanges();
    expect(slots()).toContain('2 active / 2 free / 4 total');
  });

  it('counts the ledger from the same board truth as the project rows', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    // The daemon's own activeTaskCount disagrees with the board on purpose:
    // header and rows must both follow the board, or they contradict each other.
    fixture.componentRef.setInput('host', { ...HOST, activeTaskCount: 3 });
    fixture.componentRef.setInput('boardActiveSlots', 2);
    fixture.componentRef.setInput('projectSlots', [
      { projectName: 'Agent Studio', activeSlots: 2 },
    ]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-slots"]')?.textContent)
      .toContain('2 active / 2 free / 4 total');
    expect(fixture.componentInstance.attributedSlots())
      .toBe(fixture.componentInstance.activeSlots());
  });

  it('says so instead of inventing a total when no ceiling is published', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', {
      ...HOST,
      runtimeCapacity: null,
      activeTaskCount: 7,
      availableSlots: 1,
      effectiveMaxParallelism: 8,
    });
    fixture.componentRef.setInput('boardActiveSlots', 7);
    fixture.detectChanges();

    const slots = fixture.nativeElement
      .querySelector('[data-testid="remote-host-slots"]')?.textContent;
    expect(slots).toContain('7 active / capacity not reported');
    expect(slots).not.toContain('total');
    expect(fixture.nativeElement.querySelector('[data-testid="remote-host-capacity-input"]'))
      .toBeNull();
  });

  it('offers to set a first ceiling on a host that never published one', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', {
      ...HOST,
      runtimeCapacity: null,
      effectiveMaxParallelism: 8,
    });
    let change: RuntimeCapacityChange | null = null;
    fixture.componentInstance.capacityChange.subscribe(value => { change = value; });
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;

    // Pre-filled from what the daemon says it runs, so the first published
    // ceiling describes the host instead of guessing.
    expect((element.querySelector(
      '[data-testid="remote-host-capacity-seed-input"]') as HTMLInputElement).value).toBe('8');
    (element.querySelector('[data-testid="remote-host-capacity-seed-save"]') as HTMLButtonElement)
      .click();

    expect(change).toEqual({
      id: 'runner-a',
      maxParallelism: 8,
      targetLoadPercent: 80,
      rampStrategy: 'balanced',
    });
  });

  it('lists which projects hold the shared ceiling', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', { ...HOST, activeTaskCount: 3 });
    fixture.componentRef.setInput('boardActiveSlots', 3);
    fixture.componentRef.setInput('projectSlots', [
      { projectName: 'Agent Studio', activeSlots: 2 },
      { projectName: 'Quality Studio', activeSlots: 1 },
    ]);
    fixture.detectChanges();

    const rows = fixture.nativeElement
      .querySelector('[data-testid="remote-host-project-slots"]')?.textContent ?? '';
    expect(rows).toContain('Agent Studio');
    expect(rows).toContain('2 of 4');
    expect(rows).toContain('Quality Studio');
    expect(rows).toContain('1 of 4');
  });

  it('marks a central change as waiting until the daemon reports adoption', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', {
      ...HOST,
      runtimeCapacity: { ...HOST.runtimeCapacity!, version: 2 },
    });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector(
      '[data-testid="remote-host-capacity-awaiting-adoption"]',
    )).toBeTruthy();
  });

  it('edits the versioned project allowlist without creating a runner-side resolver', async () => {
    await TestBed.configureTestingModule({
      imports: [RuntimeCapacityEditorComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RuntimeCapacityEditorComponent);
    fixture.componentRef.setInput('host', HOST);
    let change: HostProjectPolicyChange | null = null;
    fixture.componentInstance.projectPolicyChange.subscribe(value => { change = value; });
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    const ids = element.querySelector(
      '[data-testid="remote-host-project-policy-ids"]') as HTMLInputElement;
    ids.value = 'PROJ-002, PROJ-003, PROJ-002';
    ids.dispatchEvent(new Event('input'));
    (element.querySelector(
      '[data-testid="remote-host-project-policy-save"]') as HTMLButtonElement).click();

    expect(change).toEqual({
      id: 'runner-a',
      allowAllProjects: false,
      allowedProjectIds: ['PROJ-002', 'PROJ-003'],
      expectedVersion: 2,
    });
  });
});
