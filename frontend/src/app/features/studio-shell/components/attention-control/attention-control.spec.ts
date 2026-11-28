import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { WorkspaceOverlaysService } from '../../../shell';
import { StudioTabStateService } from '../../services/studio-tab-state.service';
import type { OperatorAttentionItem } from '../../../operator-attention';
import { NowTickService } from '../../../../services/now-tick.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { AttentionControlComponent } from './attention-control';
import { CredentialInventoryService } from '../../../credentials/credential-inventory.service';

const items: OperatorAttentionItem[] = [
  { id: 'h1', type: 'human-review.ready', project: 'alpha', cardKey: 'AGT-2', cards: ['AGT-2'],
    reason: 'AGT-2 is ready for human review.', observedAt: '2026-11-09T09:00:00Z', suggestedOperations: [] },
  { id: 'd1', type: 'decision-card.opened', project: 'alpha', cardKey: 'AGT-3', cards: ['AGT-3'],
    reason: 'Which store?', observedAt: '2026-11-09T08:00:00Z', suggestedOperations: [] },
  { id: 's1', type: 'backend.stalled', project: null, cardKey: null, cards: [],
    reason: 'agent-runner-01 refuses claims.', observedAt: '2026-11-09T07:00:00Z', suggestedOperations: [] },
];

describe('AttentionControlComponent', () => {
  afterEach(() => { TestBed.resetTestingModule(); vi.restoreAllMocks(); });

  async function setup(backupState: 'ok' | 'disabled' = 'ok', dossierCount = 0,
    attentionItems: OperatorAttentionItem[] = items, initialFailure = false) {
    const now = signal(Date.now());
    const openReferenceOrNotify = vi.fn(() => true);
    const overlays = { open: vi.fn() };
    await TestBed.configureTestingModule({
      imports: [AttentionControlComponent],
      providers: [
        provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        { provide: NowTickService, useValue: { now } },
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify } },
        { provide: WorkspaceOverlaysService, useValue: overlays },
      ],
    }).compileComponents();
    const http = TestBed.inject(HttpTestingController);
    const tabs = TestBed.inject(StudioTabStateService);
    const openTab = vi.spyOn(tabs, 'open').mockImplementation(() => undefined);
    const fixture = TestBed.createComponent(AttentionControlComponent);
    fixture.detectChanges();
    http.expectOne('/api/workbenches').flush({ items: dossierCount ? [{ projectName: 'alpha', workbench: {
      id: 'dossier-1', key: 'DOC-1', title: 'Concept Dossier', status: 'decision-pending',
      openDecisionCount: dossierCount, updatedAtUtc: '2026-11-09T09:00:00Z',
    } }] : [] });
    http.expectOne('/api/operator/backups').flush({ state: backupState, stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    TestBed.inject(CredentialInventoryService).state.set('unsupported');
    const main = http.expectOne('/api/operator/attention');
    if (initialFailure) main.flush({}, { status: 503, statusText: 'Unavailable' });
    else main.flush({ items: attentionItems, count: attentionItems.length });
    await fixture.whenStable();
    return { fixture, http, openReferenceOrNotify, overlays, openTab, now };
  }

  function rows(): HTMLElement[] {
    return Array.from(document.querySelectorAll<HTMLElement>('[data-testid="attention-row"]'));
  }

  function flushReadiness(http: HttpTestingController): void {
    http.expectOne('/api/tasks/acceptance-readiness').flush({
      checkedAtUtc: '2026-11-09T09:01:00Z', counts: { 'no-content-verdict': 1 }, items: [{
        taskKey: 'AGT-2', projectName: 'alpha', readiness: 'no-content-verdict',
        headline: 'No content verdict', reason: 'The review has no content verdict.',
      }],
    });
  }

  it('opens Access for ended elevation without opening a task or project tab', async () => {
    const item: OperatorAttentionItem = { id: 'elevation-ended', type: 'elevation.rate-ended', project: null,
      cardKey: null, cards: [], reason: 'Elevation ended after rate refusals.',
      observedAt: '2026-11-09T09:00:00Z', suggestedOperations: [] };
    const { fixture, http, overlays, openTab, openReferenceOrNotify } = await setup('ok', 0, [item]);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [item], count: 1 });
    http.expectNone('/api/tasks/acceptance-readiness');
    http.expectOne('/api/operator/backups').flush({ state: 'ok', stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    await fixture.whenStable();
    const action = document.querySelector<HTMLButtonElement>('[data-testid="attention-row"] button');
    expect(action).not.toBeNull();
    action!.click();
    expect(overlays.open).toHaveBeenCalledExactlyOnceWith('access');
    expect(openTab).toHaveBeenCalledExactlyOnceWith({ kind: 'workspace-settings' });
    expect(openReferenceOrNotify).not.toHaveBeenCalled();
    expect(fixture.componentInstance.open()).toBe(false);
  });

  it('updates policy recommendations without changing the owner-item identity', async () => {
    const item: OperatorAttentionItem = { id: 'expired', type: 'post-merge.red-incident',
      project: 'alpha', cardKey: 'AGT-1', cards: ['AGT-1'], reason: 'Quarantine expired.',
      observedAt: '2026-11-09T09:00:00Z', suggestedOperations: ['card.inspect'],
      ownerOptions: [{ id: 'report', label: 'report-only with a card', recommended: false }] };
    const { fixture, http } = await setup('ok', 0, [item]);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [{ ...item,
      ownerOptions: [{ ...item.ownerOptions![0], recommended: true }] }], count: 1 });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-owner-options"]')?.textContent)
      .toContain('report-only with a card Recommended');
    expect(rows()).toHaveLength(1);
  });

  it('keeps an overlap wait after refusal and removes it only after Run anyway succeeds', async () => {
    const item: OperatorAttentionItem = { id: 'dispatch.overlap-starved|alpha/AGT-3988|state', type: 'dispatch.overlap-starved',
      project: 'alpha', cardKey: 'AGT-3988', cards: ['AGT-3988', 'AGT-3481'], reason: 'AGT-3988 waits for AGT-3481.',
      observedAt: '2026-11-24T03:26:04Z', suggestedOperations: ['dispatch.run-anyway'] };
    const { fixture, http } = await setup('ok', 0, [item]);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [item], count: 1 });
    await fixture.whenStable();
    const button = document.querySelector<HTMLButtonElement>('[data-testid="attention-run-anyway"]')!;
    button.click();
    const refused = http.expectOne('/api/projects/alpha/dispatch/run-anyway');
    expect(refused.request.body).toEqual({ fact: item.id });
    refused.flush({}, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    expect(rows()).toHaveLength(1);
    expect(document.querySelector('[data-testid="attention-overlap-error"]')).not.toBeNull();
    button.click();
    http.expectOne('/api/projects/alpha/dispatch/run-anyway').flush({ permitted: true });
    await fixture.whenStable();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    http.match('/api/operator/backups').forEach(request => request.flush({}));
    await fixture.whenStable();
    expect(rows()).toHaveLength(0);
    http.verify();
  });

  it('shows the open incident and links to its card and project Operations', async () => {
    const incident: OperatorAttentionItem = {
      id: 'environment.incident-open|alpha|production', type: 'environment.incident-open',
      project: 'alpha', cardKey: 'health-production-1', cards: ['health-production-1'],
      title: 'alpha · Production incident', reason: 'Production since 2026-11-09 08:00 UTC: http: HTTP 503',
      observedAt: '2026-11-09T08:00:00Z', suggestedOperations: [],
    };
    const { fixture, http, openReferenceOrNotify, openTab } = await setup('ok', 0, [incident]);
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="studio-attention-trigger"]')!.click();
    http.expectOne('/api/operator/attention').flush({ items: [incident], count: 1 });
    await fixture.whenStable();
    const row = document.querySelector<HTMLElement>('[data-attention-type="environment.incident-open"]')!;
    expect(row.textContent).toContain('HTTP 503');
    expect(row.querySelector('[data-testid="attention-incident-status"]')?.textContent).toContain('Incident open');
    row.querySelector<HTMLButtonElement>('[data-testid="attention-open-operations"]')!.click();
    expect(openTab).toHaveBeenCalledWith({ kind: 'hub', projectName: 'alpha', section: 'operations' });

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="studio-attention-trigger"]')!.click();
    http.expectOne('/api/operator/attention').flush({ items: [incident], count: 1 });
    await fixture.whenStable();
    document.querySelector<HTMLButtonElement>('[data-testid="attention-row-action"]')!.click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('health-production-1', { focusDecision: false });
  });

  it('shows review host drift in the status bar and opens the affected host', async () => {
    const originalUrl = window.location.href;
    const drift: OperatorAttentionItem = {
      id: 'review-host.drift|review-1', type: 'review-host.drift', project: null, cardKey: null,
      cards: ['AGT-1'], title: 'Review host differs from Stable',
      reason: 'Review host first-run-review graded AGT-1 on another commit.',
      observedAt: '2026-11-09T09:00:00Z', suggestedOperations: [],
      evidence: [{ label: 'Review host', href: '/workspace/settings/environment?host=first-run-review' }],
    };
    const { fixture, http, overlays } = await setup('ok', 0, [drift]);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="review-host-drift-cue"]')?.textContent)
      .toContain('Review host');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="studio-attention-trigger"]')!.click();
    http.expectOne('/api/operator/attention').flush({ items: [drift], count: 1 });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-row"]')?.textContent).toContain('AGT-1');
    document.querySelector<HTMLButtonElement>('[data-testid="attention-row-action"]')!.click();
    expect(overlays.open).toHaveBeenCalledWith('remote-hosts');
    expect(window.location.hash).toBe('#/workspace/settings/environment?host=first-run-review');
    history.replaceState(null, '', originalUrl);
  });

  it('shows completion readiness for each review card and updates the ready count after a decision', async () => {
    const reviewItems = [items[0], { ...items[0], id: 'h2', cardKey: 'AGT-4', cards: ['AGT-4'] }];
    const { fixture, http } = await setup('ok', 0, reviewItems);
    const trigger = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement;

    trigger.click();
    http.expectOne('/api/operator/attention').flush({ items: reviewItems, count: reviewItems.length });
    const check = http.expectOne('/api/tasks/acceptance-readiness');
    expect(check.request.method).toBe('POST');
    expect(check.request.body.items).toEqual([
      { jobId: 'AGT-2', project: 'alpha' }, { jobId: 'AGT-4', project: 'alpha' },
    ]);
    check.flush({ checkedAtUtc: '2026-11-09T09:01:00Z', counts: { ready: 1, 'no-content-verdict': 1 }, items: [
      { taskKey: 'AGT-2', projectName: 'alpha', readiness: 'no-content-verdict',
        headline: 'No content verdict', reason: 'The review has no content verdict.' },
      { taskKey: 'AGT-4', projectName: 'alpha', readiness: 'ready',
        headline: 'Ready to accept', reason: 'All completion checks pass.' },
    ] });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-review-ready-count"]')?.textContent).toContain('1 of 2 ready to accept');
    expect(rows()[0].textContent).toContain('No content verdict');
    expect(rows()[0].textContent).toContain('The review has no content verdict.');
    expect(rows()[1].textContent).toContain('Ready to accept');

    trigger.click();
    trigger.click();
    http.expectOne('/api/operator/attention').flush({ items: reviewItems, count: reviewItems.length });
    http.expectOne('/api/tasks/acceptance-readiness').flush({
      checkedAtUtc: '2026-11-09T09:02:00Z', counts: { ready: 2 }, items: [
        { taskKey: 'AGT-2', projectName: 'alpha', readiness: 'ready', headline: 'Ready to accept', reason: 'Decision recorded.' },
        { taskKey: 'AGT-4', projectName: 'alpha', readiness: 'ready', headline: 'Ready to accept', reason: 'All completion checks pass.' },
      ],
    });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-review-ready-count"]')?.textContent).toContain('2 of 2 ready to accept');
    expect(rows()[0].textContent).toContain('Decision recorded.');
  });

  it('counts exactly the rows the list shows, grouped in class order', async () => {
    const { fixture, http } = await setup();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim()).toBe('2');
    // The accessible name includes both visible counts.
    const trigger = host.querySelector<HTMLButtonElement>('[data-testid="studio-attention-trigger"]')!;
    expect(host.querySelector('[data-testid="studio-attention-notice-count"]')?.textContent?.trim()).toBe('1');
    expect(trigger.getAttribute('aria-label')).toBe('Needs you 2 Notices 1, 2 decisions need you');

    (host.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    flushReadiness(http);
    await fixture.whenStable();

    expect(rows()).toHaveLength(3);
    const groups = Array.from(document.querySelectorAll('[data-testid^="attention-group-"]'))
      .map(group => group.getAttribute('data-testid'));
    expect(groups).toEqual(['attention-group-decision', 'attention-group-human-review', 'attention-group-host']);
    expect(rows().map(row => row.querySelector('[data-testid="attention-row-action"]')?.textContent?.trim()))
      .toEqual(['Decide', 'Review', 'Open hosts']);

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(rows()).toHaveLength(0);
  });

  it('removes archived project facts and Dossiers from Needs you immediately', async () => {
    const { fixture } = await setup('ok', 1);
    const attention = fixture.componentInstance.attention;
    attention.setArchivedProjects(['alpha']);
    fixture.detectChanges();

    expect(attention.items().map(item => item.id)).toEqual(['s1']);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim())
      .toBe('0');
  });

  it('adds backup and credential warnings to the visible installation group', async () => {
    const { fixture, http, overlays } = await setup('disabled');
    TestBed.inject(CredentialInventoryService).state.set('ready');
    TestBed.inject(CredentialInventoryService).inventory.set({
      generatedAt: '2026-11-09T09:00:00Z',
      items: [{ id: 'provider', label: 'Provider token', state: 'expiring', detail: 'Expires soon.' } as never],
    });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim()).toBe('2');
    (host.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/backups').flush({ state: 'disabled', stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    flushReadiness(http);
    await fixture.whenStable();
    const group = document.querySelector('[data-testid="attention-group-installation"]') as HTMLElement;
    expect(group.querySelector('[data-testid="attention-count-in-group"]')?.textContent?.trim()).toBe('2');
    expect(group.textContent).toContain('Backups off');
    expect(group.textContent).toContain('Expires soon.');
    expect(group.querySelectorAll<HTMLButtonElement>('[data-testid="attention-row-action"]')[0].textContent).toContain('Open operations');
    const action = group.querySelectorAll<HTMLButtonElement>('[data-testid="attention-row-action"]')[1];
    expect(action.textContent).toContain('Open credentials');
    action.click();
    expect(overlays.open).toHaveBeenCalledWith('credentials');
  });

  it('adds open Dossier gates with their decision count and opens the Dossier', async () => {
    const { fixture, http, openTab } = await setup('ok', 2);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-attention-count"]')?.textContent?.trim()).toBe('3');
    (host.querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement).click();
    http.expectOne('/api/operator/backups').flush({ state: 'ok', stale: false,
      generatedAt: '2026-11-09T09:00:00Z', warnings: [], backups: [], offHost: {} });
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    flushReadiness(http);
    await fixture.whenStable();
    const group = document.querySelector('[data-testid="attention-group-dossier"]') as HTMLElement;
    expect(group.textContent).toContain('2 open decisions');
    (group.querySelector('[data-testid="attention-row-action"]') as HTMLButtonElement).click();
    expect(openTab).toHaveBeenCalledWith({ kind: 'workbench', projectName: 'alpha',
      workbenchId: 'dossier-1', title: 'Concept Dossier', key: 'DOC-1' });
  });

  it('opens the card of a review row and the hosts surface of a host row', async () => {
    const { fixture, http, openReferenceOrNotify, overlays, openTab } = await setup();
    const trigger = (fixture.nativeElement as HTMLElement)
      .querySelector('[data-testid="studio-attention-trigger"]') as HTMLButtonElement;

    trigger.click();
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    flushReadiness(http);
    await fixture.whenStable();
    (rows()[1].querySelector('[data-testid="attention-row-action"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('AGT-2', { focusDecision: true });
    expect(rows()).toHaveLength(0);

    trigger.click();
    http.expectOne('/api/operator/attention').flush({ items, count: items.length });
    flushReadiness(http);
    await fixture.whenStable();
    (rows()[2].querySelector('[data-testid="attention-row-action"]') as HTMLButtonElement).click();
    expect(overlays.open).toHaveBeenCalledWith('remote-hosts');
    expect(openTab).toHaveBeenCalledWith({ kind: 'workspace-settings' });
  });

  it.each([{ retained: [] }, { retained: [items[1], items[2]] }])('keeps visible facts after a failed subsequent read: %j', async ({ retained }) => {
    const { fixture, http } = await setup('disabled', 0, retained);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({}, { status: 503, statusText: 'Unavailable' });
    http.expectOne('/api/operator/backups').flush({}, { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();
    expect(rows()).toHaveLength(retained.length + 1);
    expect(fixture.componentInstance.count() + fixture.componentInstance.attention.noticeCount()).toBe(rows().length);
    expect(document.querySelector('[data-testid="attention-list-coverage"]')?.textContent).toContain('stale');
    expect(document.querySelector('[data-testid="attention-list-coverage"]')?.textContent).toContain('Backups');
    expect(fixture.componentInstance.triggerLabel()).toContain('Partial coverage');
    expect(document.querySelector('[data-testid="attention-list-empty"]')).toBeNull();
    fixture.componentInstance.attention.refresh();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 999 });
    http.expectOne('/api/operator/backups').flush({ state: 'ok', warnings: [], backups: [], offHost: {} });
    await fixture.whenStable();
    expect(rows()).toHaveLength(0);
    expect(fixture.componentInstance.triggerLabel()).toBe('Nothing needs you');
  });

  it('shows checking coverage before any read completes', async () => {
    await TestBed.configureTestingModule({
      imports: [AttentionControlComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    const fixture = TestBed.createComponent(AttentionControlComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance.triggerLabel()).toBe('Checking coverage');
    fixture.componentInstance.toggle();
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-list-coverage"]')?.textContent).toContain('pending');
    expect(document.querySelector('[data-testid="attention-list-empty"]')).toBeNull();
    expect(fixture.componentInstance.count()).toBe(0);
  });

  it.each([false, true])('shows unavailable initial coverage with auxiliary facts: %s', async auxiliary => {
    const { fixture, http } = await setup(auxiliary ? 'disabled' : 'ok', auxiliary ? 2 : 0, [], true);
    expect(fixture.componentInstance.triggerLabel()).toContain('Partial coverage');
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({}, { status: 503, statusText: 'Unavailable' });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-list-coverage"]')?.textContent).toContain('Attention: unavailable');
    expect(rows()).toHaveLength(auxiliary ? 2 : 0);
    expect(fixture.componentInstance.count() + fixture.componentInstance.attention.noticeCount()).toBe(rows().length);
    expect(document.querySelector('[data-testid="attention-list-empty"]')).toBeNull();
  });

  it('waits for every source before declaring all clear and retains Dossier and credential facts', async () => {
    const { fixture, http } = await setup('ok', 2, []);
    const inventory = TestBed.inject(CredentialInventoryService);
    inventory.state.set('ready');
    inventory.inventory.set({ generatedAt: new Date().toISOString(), items: [
      { id: 'provider', label: 'Provider', state: 'missing', detail: 'Missing.' } as never,
    ] });
    await fixture.whenStable();
    inventory.inventory.set(null);
    inventory.state.set('unavailable');
    vi.spyOn(Date, 'now').mockReturnValue(Date.now() + 31_000);
    fixture.componentInstance.toggle();
    http.expectOne('/api/workbenches').flush({}, { status: 503, statusText: 'Unavailable' });
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    http.expectOne('/api/operator/backups').flush({});
    await fixture.whenStable();
    expect(rows()).toHaveLength(2);
    expect(fixture.componentInstance.count()).toBe(1);
    const coverage = document.querySelector('[data-testid="attention-list-coverage"]')?.textContent;
    expect(coverage).toContain('Dossiers: stale');
    expect(coverage).toContain('Credentials: stale');
    expect(coverage).toContain('Backups: stale');
    inventory.state.set('loading');
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-list-coverage"]')?.textContent).toContain('Credentials: stale');
  });

  it.each([0, 1, 3])('opens installation backups independently of %i projects', async count => {
    const { fixture, http, overlays, openTab } = await setup('disabled', 0, []);
    vi.spyOn(TestBed.inject(ProjectLookupService), 'allProjects').mockReturnValue(
      Array.from({ length: count }, (_, index) => ({ displayName: `project-${index}` })) as never);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    await fixture.whenStable();
    document.querySelector<HTMLButtonElement>('[data-testid="attention-row-action"]')!.click();
    expect(overlays.open).toHaveBeenCalledWith('operations');
    expect(openTab).toHaveBeenCalledWith({ kind: 'workspace-settings' });
    // The destination is deferred; the handoff must wait for its DOM.
    const panel = document.createElement('section');
    panel.dataset['testid'] = 'operations-backups';
    const scroll = vi.fn();
    panel.scrollIntoView = scroll;
    document.body.append(panel);
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(scroll).toHaveBeenCalledWith({ block: 'start' });
    panel.remove();
  });

  it('keeps the backup handoff active while Operations content above it is loading', async () => {
    const { fixture, http } = await setup('disabled', 0, []);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    await fixture.whenStable();
    const operations = document.createElement('section');
    operations.dataset['testid'] = 'workspace-operations';
    const loading = document.createElement('app-loading-surface');
    const panel = document.createElement('section');
    panel.dataset['testid'] = 'operations-backups';
    panel.scrollIntoView = vi.fn();
    operations.append(loading, panel);
    document.body.append(operations);
    try {
      document.querySelector<HTMLButtonElement>('[data-testid="attention-row-action"]')!.click();
      expect(panel.scrollIntoView).toHaveBeenCalledTimes(1);
      loading.remove();
      await new Promise(resolve => setTimeout(resolve, 0));
      expect(panel.scrollIntoView).toHaveBeenCalledTimes(2);
    } finally { operations.remove(); }
  });

  it('ages retained and newly polled rows on the shared clock only while open', async () => {
    const start = Date.parse('2026-11-27T10:00:00Z');
    vi.spyOn(Date, 'now').mockReturnValue(start);
    const first = { ...items[1], observedAt: new Date(start).toISOString() };
    const { fixture, http, now } = await setup('ok', 0, [first]);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [first], count: 1 });
    await fixture.whenStable();
    expect(rows()[0].textContent).toContain('just now');
    now.set(start + 120_000);
    await fixture.whenStable();
    expect(rows()[0].textContent).toContain('2 min');
    fixture.componentInstance.attention.refresh();
    const second = { ...items[2], observedAt: new Date(start + 120_000).toISOString() };
    http.expectOne('/api/operator/attention').flush({ items: [first, second], count: 2 });
    now.set(start + 180_000);
    await fixture.whenStable();
    expect(rows()[0].textContent).toContain('3 min');
    expect(rows()[1].textContent).toContain('1 min');
    fixture.componentInstance.close();
    const closed = fixture.componentInstance.now();
    now.set(start + 240_000);
    expect(fixture.componentInstance.now()).toBe(closed);
  });

  it('says so when every source has completed and nothing needs the operator', async () => {
    const { fixture, http } = await setup('ok', 0, []);
    fixture.componentInstance.toggle();
    http.expectOne('/api/operator/attention').flush({ items: [], count: 0 });
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="attention-list-empty"]')?.textContent).toContain('Nothing needs you');
    expect(rows()).toHaveLength(0);
  });
});
