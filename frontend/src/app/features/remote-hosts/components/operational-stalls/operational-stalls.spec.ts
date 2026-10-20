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
});
