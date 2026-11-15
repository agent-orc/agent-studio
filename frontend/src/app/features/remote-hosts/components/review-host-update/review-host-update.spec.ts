import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ReviewHostUpdateComponent } from './review-host-update';

const expectedCommit = 'b'.repeat(40);
const expectedImageRef = `registry.example/review@sha256:${'b'.repeat(64)}`;
const target = { clientId: 'review-1', name: 'Review one', expectedCommit, expectedImageRef };

describe('ReviewHostUpdateComponent', () => {
  it('can cancel a drain before the switch without recording completion', async () => {
    TestBed.configureTestingModule({ imports: [ReviewHostUpdateComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] });
    const fixture = TestBed.createComponent(ReviewHostUpdateComponent);
    fixture.componentRef.setInput('target', target);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    const start = component.start();
    http.expectOne('/api/clients/review-1/drain').flush({});
    await Promise.resolve();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: true, running: [] });
    await start;
    const cancel = component.cancel();
    http.expectOne('/api/clients/review-1/resume').flush({});
    await Promise.resolve();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: false, drained: false, running: [] });
    await cancel;
    expect(component.stage()).toBe('ready');
    expect(component.switchStartedAt()).toBeNull();
    expect(component.proofVerified()).toBe(false);
    http.verify();
  });

  it('refuses a busy review, then requires a fresh heartbeat before resume', async () => {
    TestBed.configureTestingModule({ imports: [ReviewHostUpdateComponent], providers: [
      provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
    ] });
    const fixture = TestBed.createComponent(ReviewHostUpdateComponent);
    fixture.componentRef.setInput('target', target);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    const http = TestBed.inject(HttpTestingController);
    expect(fixture.nativeElement.querySelector('[data-testid="review-host-expected-digest"]')?.textContent)
      .toContain(expectedImageRef);

    const start = component.start();
    http.expectOne('/api/clients/review-1/drain').flush({});
    await Promise.resolve();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: false,
      running: [{ kind: 'review', attemptId: 'attempt-1', taskKey: 'AGT-1' }] });
    await start;
    expect(component.stage()).toBe('blocked');
    expect(component.message()).toContain('Review AGT-1 is still active');
    http.expectNone('/api/v1/management/host-releases');

    const check = component.checkQuiet();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: true, running: [] });
    await check;
    expect(component.stage()).toBe('quiet');

    const racedSwitch = component.beginSwitch();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: false,
      running: [{ kind: 'review', attemptId: 'attempt-2', taskKey: 'AGT-2' }] });
    await racedSwitch;
    expect(component.stage()).toBe('blocked');
    expect(component.switchStartedAt()).toBeNull();

    const quietAgain = component.checkQuiet();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: true, running: [] });
    await quietAgain;
    const switchPromise = component.beginSwitch();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: true, running: [] });
    await switchPromise;
    expect(component.stage()).toBe('switching');
    expect(component.switchStartedAt()).toBeTruthy();

    const oldReport = { observedAt: new Date().toISOString(), stable: { version: '0.9.5', commit: expectedCommit },
      behindCount: 0, hosts: [{ runnerId: 'review-1', name: 'Review one', hostId: 'machine-1', role: 'review',
        release: { releaseId: 'new', version: '0.9.5', commit: expectedCommit, imageRef: expectedImageRef },
        state: 'current', behindByHours: null, behindForHours: null, alarmDue: false, reason: 'current',
        lastSeenAt: component.switchStartedAt(), heartbeatStale: false, expectedImageRef }] };
    const firstCheck = component.verify();
    http.expectOne('/api/v1/management/host-releases').flush(oldReport);
    await firstCheck;
    expect(component.stage()).toBe('switching');
    http.expectNone('/api/clients/review-1/resume');

    const freshCheck = component.verify();
    http.expectOne('/api/v1/management/host-releases').flush({ ...oldReport, hosts: [
      { ...oldReport.hosts[0], lastSeenAt: new Date(Date.parse(component.switchStartedAt()!) + 1000).toISOString() },
    ] });
    await freshCheck;
    expect(component.stage()).toBe('verified');

    const resume = component.resume();
    http.expectOne('/api/clients/review-1/resume').flush({});
    await Promise.resolve();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: false, drained: false, running: [] });
    await resume;
    expect(component.stage()).toBe('complete');
    http.verify();
  });
});
