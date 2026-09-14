import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { beforeEach, describe, expect, it } from 'vitest';
import type { TaskExecutionLocation } from '../../models/task.model';
import { RemoteDispatchRejectionComponent } from './remote-dispatch-rejection.component';

function execution(lastRejection: unknown): TaskExecutionLocation {
  return {
    state: 'queued-remote',
    executionKind: 'none',
    connectionState: 'queued',
    leaseState: 'none',
    trustReason: 'Queued for a remote Runner.',
    lastRejection,
  } as unknown as TaskExecutionLocation;
}

describe('RemoteDispatchRejectionComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteDispatchRejectionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  function render(lastRejection: unknown): HTMLElement {
    const fixture = TestBed.createComponent(RemoteDispatchRejectionComponent);
    fixture.componentRef.setInput('execution', execution(lastRejection));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows the runner and reason without requiring a tooltip', () => {
    const root = render({
      code: 'repository-url-missing',
      runnerId: 'agent-runner-01',
      runnerName: 'agent-runner-01',
      reason: 'project has no repositoryUrl',
      rejectedAtUtc: '2026-08-08T10:00:00Z',
    });

    const alert = root.querySelector('[data-testid="remote-dispatch-rejection"]') as HTMLElement;
    expect(alert.textContent).toContain('Runner agent-runner-01 rejected:');
    expect(alert.textContent).toContain('project has no repositoryUrl');
  });

  // AGT-2818: AGT-2738 sat in 2-ready for a week with this record on disk and
  // nothing rendered. Code and instant are what make it actionable.
  it('names the refusal code and when it happened', () => {
    const root = render({
      code: 'capability-mismatch',
      runnerId: 'agent-runner-01',
      runnerName: 'agent-runner-01',
      reason: "Required capability 'task-server:connectivity' is advertised as unavailable.",
      rejectedAtUtc: '2026-09-06T19:47:45Z',
    });

    const alert = root.querySelector('[data-testid="remote-dispatch-rejection"]') as HTMLElement;
    expect(alert.getAttribute('data-rejection-code')).toBe('capability-mismatch');
    expect(root.querySelector('[data-testid="remote-dispatch-rejection-code"]')?.textContent)
      .toContain('capability-mismatch');
    expect(alert.textContent).toContain('task-server:connectivity');

    const at = root.querySelector('[data-testid="remote-dispatch-rejection-at"]')?.textContent ?? '';
    expect(at).toContain('refused');
    // The calendar date is present, not only a clock: a week-old refusal must
    // not read as "just now".
    expect(at).toContain(new Date('2026-09-06T19:47:45Z').toLocaleString());
  });

  it('falls back to the runner id when the runner has no display name', () => {
    const root = render({
      code: 'capability-mismatch',
      runnerId: 'runner-7',
      runnerName: '',
      reason: 'no capability',
      rejectedAtUtc: '2026-09-06T19:47:45Z',
    });

    expect(root.querySelector('[data-testid="remote-dispatch-rejection"]')?.textContent)
      .toContain('Runner runner-7 rejected:');
  });

  it('renders nothing when the current lane stay carries no refusal', () => {
    expect(render(null).querySelector('[data-testid="remote-dispatch-rejection"]')).toBeNull();
  });
});
