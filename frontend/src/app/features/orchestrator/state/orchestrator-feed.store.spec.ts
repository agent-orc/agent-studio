import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ensureBrowserStorage } from '../../../../testing/browser-storage';
import { OrchestratorFeedStore } from './orchestrator-feed.store';

ensureBrowserStorage();

describe('OrchestratorFeedStore', () => {
  const seenKey = 'atp.orchestrator-feed.alerts-seen-at';

  beforeEach(() => {
    localStorage.removeItem(seenKey);
    TestBed.configureTestingModule({
      providers: [OrchestratorFeedStore, provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.resetTestingModule();
    localStorage.removeItem(seenKey);
  });

  it('baselines historical alerts, then counts only alerts that arrive later', () => {
    const store = TestBed.inject(OrchestratorFeedStore);
    const http = TestBed.inject(HttpTestingController);
    store.refresh();
    http.expectOne(request => request.url.includes('/api/watcher/stalls')).flush({ count: 1, cases: [{ id: 'WCH-1', project: '_workspace', subject: 'host:kori', severity: 'critical', affectedCards: [] }] });
    expect(store.openStallCount()).toBe(1);
    http.expectOne(request => request.url.includes('/api/runner/orchestrator-feed')).flush({
      entries: [{ ts: '2026-07-30T08:00:00Z', kind: 'alert', topic: 'gate', summary: 'Historical alert' }],
    });
    expect(store.freshAlertCount()).toBe(0);

    store.refresh(true);
    http.expectOne(request => request.url.includes('/api/watcher/stalls')).flush({ count: 0, cases: [] });
    expect(store.openStallCount()).toBe(0);
    http.expectOne(request => request.url.includes('/api/runner/orchestrator-feed')).flush({
      entries: [
        { ts: '2026-07-30T08:05:00Z', kind: 'alert', topic: 'gate', summary: 'Fresh alert' },
        { ts: '2026-07-30T08:04:00Z', kind: 'decision', topic: 'route', summary: 'Routine decision' },
        { ts: '2026-07-30T08:00:00Z', kind: 'alert', topic: 'gate', summary: 'Historical alert' },
      ],
    });
    expect(store.freshAlertCount()).toBe(1);

    store.markAlertsSeen();
    expect(store.freshAlertCount()).toBe(0);
  });
});
