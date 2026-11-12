import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import type { RemoteHost } from '../../models/remote-host.model';
import { ReviewRestartHistoryComponent } from './review-restart-history';

function host(overrides: Partial<RemoteHost>): RemoteHost {
  return {
    id: 'first-run-review',
    name: 'first-run-review',
    role: 'remote',
    address: null,
    clientId: 'first-run-review',
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: null,
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: null,
    ...overrides,
  } as RemoteHost;
}

function reference(key: string): TaskReferenceStatus {
  return {
    key, exists: true, taskKey: `demo::${key}`, title: 'Lost review card', lane: '4-auto-review',
    projectId: 'demo', projectName: 'demo', projectColor: null, merge: null, reviewGrade: null,
  };
}

async function render(hosts: RemoteHost[], statuses: TaskReferenceStatus[] = []) {
  const getReferenceStatuses = vi.fn(() => of(statuses));
  const openReferenceOrNotify = vi.fn(() => true);
  await TestBed.configureTestingModule({
    imports: [ReviewRestartHistoryComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { getReferenceStatuses } },
      { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify, openTaskKey: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(ReviewRestartHistoryComponent);
  fixture.componentRef.setInput('hosts', hosts);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, element: fixture.nativeElement as HTMLElement, getReferenceStatuses, openReferenceOrNotify };
}

// AGT-3690 (S5-F14): the host detail kept only the newest restart, so a later
// clean restart replaced "1 reviews lost" with "0 reviews lost".
describe('ReviewRestartHistoryComponent', () => {
  const lossyThenClean = host({
    reviewRestarts: [
      { restartedAt: '2026-11-10T02:40:00Z', hostId: 'lab-review-host', instanceId: 'review:clean',
        reviewsLost: 0, lostAttempts: [] },
      { restartedAt: '2026-11-10T02:27:16Z', hostId: 'lab-review-host', instanceId: 'review:loss',
        reviewsLost: 1, lostAttempts: [{ attemptId: 'review_s5', taskKey: 'AGT-3648', status: 'worker-not-reported' }] },
    ],
  });

  it('lists every restart newest first and keeps the lost review after a clean restart', async () => {
    const { element } = await render([lossyThenClean], [reference('AGT-3648')]);

    const rows = [...element.querySelectorAll<HTMLElement>('[data-testid="remote-host-restart-row"]')];
    expect(rows.map(row => row.dataset['reviewsLost'])).toEqual(['0', '1']);
    expect(rows[0].textContent).toContain('No review lost');
    expect(rows[1].textContent).toContain('1 review lost');
    expect(element.querySelector('h4')?.textContent).toContain('Review restarts · 2 · 1 review lost');
  });

  it('links each lost attempt to its card through the shared task microcard', async () => {
    const { element, getReferenceStatuses } = await render([lossyThenClean], [reference('AGT-3648')]);

    expect(getReferenceStatuses).toHaveBeenCalledExactlyOnceWith(['AGT-3648']);
    const lost = element.querySelector<HTMLElement>('[data-testid="remote-host-restart-lost-attempt"]')!;
    expect(lost.dataset['taskKey']).toBe('AGT-3648');
    expect(lost.querySelector('app-task-reference-microcard')).not.toBeNull();
    expect(lost.textContent).toContain('review_s5');
  });

  it('falls back to a plain card link when the card status is unknown', async () => {
    const { element, openReferenceOrNotify } = await render([lossyThenClean], []);

    const link = element.querySelector<HTMLAnchorElement>('a[data-testid="remote-host-restart-lost-card"]')!;
    expect(link.getAttribute('href')).toBe('#/tasks/AGT-3648');
    link.click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('AGT-3648');
  });

  it('merges the restarts of every role on the machine and renders nothing without any', async () => {
    const coding = host({ id: 'first-run-coding', clientId: 'first-run-coding', name: 'first-run-coding',
      reviewRestarts: [] });
    const { element } = await render([coding, lossyThenClean]);
    expect(element.querySelectorAll('[data-testid="remote-host-restart-row"]')).toHaveLength(2);

    TestBed.resetTestingModule();
    const empty = await render([coding]);
    expect(empty.element.querySelector('[data-testid="remote-host-restart-history"]')).toBeNull();
    expect(empty.getReferenceStatuses).not.toHaveBeenCalled();
  });
});
