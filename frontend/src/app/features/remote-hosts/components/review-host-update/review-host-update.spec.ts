import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ReviewHostUpdateComponent } from './review-host-update';

const expectedCommit = 'b'.repeat(40);
const expectedImageRef = `registry.example/review@sha256:${'b'.repeat(64)}`;
const target = { clientId: 'review-1', name: 'Review one', expectedCommit, expectedImageRef };
const notDraining = { draining: false, drained: false, running: [] };

function mount(input: typeof target & { updatePending?: boolean } = target) {
  TestBed.configureTestingModule({ imports: [ReviewHostUpdateComponent], providers: [
    provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(),
  ] });
  const fixture = TestBed.createComponent(ReviewHostUpdateComponent);
  fixture.componentRef.setInput('target', input);
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance, http: TestBed.inject(HttpTestingController) };
}

/** The panel reads the server drain status once on load (AGT-3852). */
async function restore(http: HttpTestingController, status: object): Promise<void> {
  http.expectOne('/api/clients/review-1/drain-status').flush(status);
  for (let i = 0; i < 3; i++) await Promise.resolve();
}

describe('ReviewHostUpdateComponent', () => {
  it('can cancel a drain before the switch without recording completion', async () => {
    const { component, http } = mount();
    await restore(http, notDraining);
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
    const { fixture, component, http } = mount();
    await restore(http, notDraining);
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
    http.expectOne('/api/clients/review-1/deploy-switch').flush({ error: 'client-not-drained', status: {
      draining: true, drained: false, running: [{ kind: 'review', attemptId: 'attempt-2', taskKey: 'AGT-2' }] } },
    { status: 409, statusText: 'Conflict' });
    await racedSwitch;
    expect(component.stage()).toBe('blocked');
    expect(component.switchStartedAt()).toBeNull();

    const quietAgain = component.checkQuiet();
    http.expectOne('/api/clients/review-1/drain-status').flush({ draining: true, drained: true, running: [] });
    await quietAgain;
    const switchPromise = component.beginSwitch();
    const recorded = http.expectOne('/api/clients/review-1/deploy-switch');
    expect(recorded.request.method).toBe('POST');
    recorded.flush({ draining: true, drained: true, running: [],
      drainRequestedAt: '2026-11-15T09:00:00Z', switchStartedAt: new Date(Date.now() - 1000).toISOString() });
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

  it('resumes at the recorded switch after a reload and keeps verification and resume', async () => {
    const { fixture, component, http } = mount();
    const drainedAt = '2026-11-15T09:00:00Z';
    const switchedAt = '2026-11-15T09:05:00Z';
    await restore(http, { draining: true, drained: true, running: [], drainRequestedAt: drainedAt,
      switchStartedAt: switchedAt });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(component.stage()).toBe('switching');
    expect(component.progressIndex()).toBe(2);
    expect(element.querySelector('[data-testid="review-host-drained-since"] time')?.getAttribute('datetime')).toBe(drainedAt);
    expect(element.querySelector('[data-testid="review-host-switch-started"] time')?.getAttribute('datetime')).toBe(switchedAt);
    expect(element.querySelector('[data-testid="review-host-update-verify"]')).not.toBeNull();
    expect(element.textContent).not.toContain('Cancel and resume');

    const check = component.verify();
    http.expectOne('/api/v1/management/host-releases').flush({ observedAt: '2026-11-15T09:06:01Z',
      stable: { version: '0.9.5', commit: expectedCommit }, behindCount: 0, hosts: [{ runnerId: 'review-1',
        name: 'Review one', hostId: 'machine-1', role: 'review', state: 'current', behindByHours: null,
        behindForHours: null, alarmDue: false, reason: 'current', lastSeenAt: '2026-11-15T09:06:00Z',
        heartbeatStale: false, expectedImageRef,
        release: { releaseId: 'new', version: '0.9.5', commit: expectedCommit, imageRef: expectedImageRef } }] });
    await check;
    fixture.detectChanges();
    expect(component.stage()).toBe('verified');
    expect(element.querySelector('[data-testid="review-host-update-resume"]')).not.toBeNull();
    http.verify();
  });

  it('resumes at the quiet point after a reload with cancel and resume available', async () => {
    const { fixture, component, http } = mount();
    await restore(http, { draining: true, drained: true, running: [], drainRequestedAt: '2026-11-15T09:00:00Z' });
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(component.stage()).toBe('quiet');
    expect(element.querySelector('[data-testid="review-host-update-switch"]')).not.toBeNull();
    expect(element.querySelector('[data-testid="review-host-drained-since"]')).not.toBeNull();
    expect(element.textContent).toContain('Cancel and resume');

    const cancel = component.cancel();
    http.expectOne('/api/clients/review-1/resume').flush({});
    await Promise.resolve();
    http.expectOne('/api/clients/review-1/drain-status').flush(notDraining);
    await cancel;
    expect(component.stage()).toBe('ready');
    http.verify();
  });

  it('shows the same step in two sessions and adopts a switch the other operator started', async () => {
    const quiet = { draining: true, drained: true, running: [], drainRequestedAt: '2026-11-15T09:00:00Z' };
    const first = mount();
    await restore(first.http, quiet);
    const second = TestBed.createComponent(ReviewHostUpdateComponent);
    second.componentRef.setInput('target', target);
    second.detectChanges();
    await restore(first.http, quiet);
    expect(first.component.stage()).toBe('quiet');
    expect(second.componentInstance.stage()).toBe('quiet');

    const switchedAt = '2026-11-15T09:05:00Z';
    const started = first.component.beginSwitch();
    first.http.expectOne('/api/clients/review-1/deploy-switch').flush({ ...quiet, switchStartedAt: switchedAt });
    await started;
    const adopted = second.componentInstance.beginSwitch();
    first.http.expectOne('/api/clients/review-1/deploy-switch').flush({ ...quiet, switchStartedAt: switchedAt });
    await adopted;
    expect(first.component.stage()).toBe('switching');
    expect(second.componentInstance.stage()).toBe('switching');
    expect(second.componentInstance.switchStartedAt()).toBe(switchedAt);
    first.http.verify();
  });

  it('keeps a no longer drifting review host visible only for a recorded switch', async () => {
    const settled = { ...target, updatePending: false };
    const idle = mount(settled);
    await restore(idle.http, { draining: true, drained: true, running: [] });
    idle.fixture.detectChanges();
    expect(idle.component.active()).toBeNull();
    expect(idle.fixture.nativeElement.querySelector('[data-testid="review-host-update"]')).toBeNull();

    const switched = TestBed.createComponent(ReviewHostUpdateComponent);
    switched.componentRef.setInput('target', settled);
    switched.detectChanges();
    await restore(idle.http, { draining: true, drained: true, running: [], switchStartedAt: '2026-11-15T09:05:00Z' });
    switched.detectChanges();
    expect(switched.componentInstance.stage()).toBe('switching');
    expect(switched.nativeElement.querySelector('[data-testid="review-host-update-verify"]')).not.toBeNull();
    idle.http.verify();
  });
});
