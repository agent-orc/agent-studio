import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { OrchestratorFeedStore } from '../../../orchestrator';
import { OperationalStallsComponent } from './operational-stalls';

describe('OperationalStallsComponent', () => {
  it('shows open host and container nodes with their affected card counts', async () => {
    TestBed.configureTestingModule({
      imports: [OperationalStallsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(OperationalStallsComponent);
    const store = TestBed.inject(OrchestratorFeedStore);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/watcher/interventions?limit=50').flush({ count: 0, interventions: [] });
    store.refresh();
    http.expectOne(request => request.url.includes('/api/watcher/stalls')).flush({
      count: 3,
      cases: [
        { id: 'one', project: '_workspace', subject: 'host:kori-review-01', severity: 'critical', affectedCards: ['AGT-1', 'AGT-2'], blockedCount: 2,
          lastSummary: 'Review host stopped reporting.', suggestedAction: 'Restore its heartbeat.', sinceUtc: '2026-10-16T19:00:00Z' },
        { id: 'two', project: '_workspace', subject: 'container:engine-host:engine', severity: 'critical', affectedCards: [],
          lastSummary: 'Engine release differs.', suggestedAction: 'Reconcile the image.' },
        { id: 'three', project: 'KORI', subject: 'card:AGT-3:review', severity: 'warning', affectedCards: ['AGT-3'] },
      ],
    });
    http.expectOne(request => request.url.includes('/api/runner/orchestrator-feed')).flush({ entries: [] });
    await fixture.whenStable();
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('kori-review-01');
    expect(text).toContain('engine on engine-host');
    expect(text).toContain('2 affected cards');
    expect(text).toContain('Restore its heartbeat.');
    expect(text).not.toContain('card:AGT-3');
    http.verify();
  });

  it('shows the AI Operator receipt on its stall and undoes it on request', async () => {
    TestBed.configureTestingModule({
      imports: [OperationalStallsComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(OperationalStallsComponent);
    const store = TestBed.inject(OrchestratorFeedStore);
    const http = TestBed.inject(HttpTestingController);
    const receipt = {
      id: 'si-1', subject: 'host:agent-runner-01', action: 'lower-admission', autonomy: 'A1', status: 'applied',
      observed: 'agent-runner-01 ran with load 46.7 on 12 cores.', done: 'I lowered its admission from 8 to 6 parallel runs.',
      why: 'Fewer parallel builds.', before: 'admission 8', after: 'admission 6', undo: 'Undo restores 8.', chain: [], undoable: true,
    };
    const rows = [
      { receipt, kind: 'host-overload', host: 'agent-runner-01', project: '_workspace', decidedBy: 'monolith',
        decidedAtUtc: '2026-10-16T19:52:00Z', affectedCards: [], headline: 'h' },
      { receipt: { ...receipt, id: 'si-0', subject: 'host:kori-review-01:review', action: 'restart-service', status: 'verified',
        observed: 'review-daemon on kori-review-01 had 3 idle slots.', done: 'I restarted review-daemon on kori-review-01.', undoable: false },
        kind: 'idle-capacity', host: 'kori-review-01', project: '_workspace', decidedBy: 'engine',
        decidedAtUtc: '2026-10-16T18:00:00Z', affectedCards: [], headline: 'h' },
    ];
    http.expectOne('/api/watcher/interventions?limit=50').flush({ count: 2, interventions: rows });
    store.refresh();
    http.expectOne(request => request.url.includes('/api/watcher/stalls')).flush({
      count: 1,
      cases: [{ id: 'one', project: '_workspace', subject: 'host:agent-runner-01', severity: 'critical', affectedCards: [],
        lastSummary: 'Host agent-runner-01 exceeds its 95% CPU target.' }],
    });
    http.expectOne(request => request.url.includes('/api/runner/orchestrator-feed')).flush({ entries: [] });
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const stall = element.querySelector('[data-testid="operational-stalls"]')!;
    expect(stall.textContent).toContain('I lowered its admission from 8 to 6 parallel runs.');
    const settled = element.querySelector('[data-testid="stall-interventions"]')!;
    expect(settled.textContent).toContain('I restarted review-daemon on kori-review-01.');
    expect(settled.textContent).toContain('Verified by its effect');
    (stall.querySelector('[data-testid="stall-intervention-undo"]') as HTMLButtonElement).click();
    http.expectOne({ method: 'POST', url: '/api/watcher/interventions/si-1/undo' }).flush({ message: 'undone' });
    http.expectOne('/api/watcher/interventions?limit=50').flush({ count: 0, interventions: [] });
    http.verify();
  });
});
