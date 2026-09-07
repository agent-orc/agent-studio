import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import type { TaskIntegrationStatus, IntegrationStatusValue } from '../../features/git';
import { IntegrationStatusBadgeComponent } from './integration-status-badge.component';
import { TaskService } from '../../services/task.service';
import { NotificationService } from '../../services/notification.service';

function integration(
  status: IntegrationStatusValue,
  overrides: Partial<TaskIntegrationStatus> = {},
): TaskIntegrationStatus {
  return {
    status,
    deliveryRef: null,
    sha: status === 'integrated' ? 'abc1234' : null,
    integrationBranch: 'develop',
    detail: null,
    ...overrides,
  };
}

describe('IntegrationStatusBadgeComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [IntegrationStatusBadgeComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
  });

  function render(value: TaskIntegrationStatus | null) {
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', value);
    fixture.detectChanges();
    return fixture;
  }

  it('renders integrated as green "merged @sha"', () => {
    const fixture = render(integration('integrated', { sha: 'deadbee' }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('merged @deadbee');
    expect(badge.dataset['kind']).toBe('integrated');
    expect(badge.classList.contains('integration-badge--acute')).toBe(false);
  });

  it('renders repository-scoped delivery counts and target branches', () => {
    const fixture = render(integration('integrated', {
      repositories: [
        {
          repository: 'agent-studio',
          commits: Array.from({ length: 5 }, (_, index) => ({
            sha: `studio-${index}`, onIntegrationBranch: true, onReleaseBranch: true,
          })),
          integrationBranch: 'develop', releaseBranch: 'main',
          onIntegrationBranch: true, onReleaseBranch: true,
          detail: '5/5 on develop and main.',
        },
        {
          repository: 'runner',
          commits: Array.from({ length: 4 }, (_, index) => ({
            sha: `runner-${index}`, onIntegrationBranch: true, onReleaseBranch: true,
          })),
          integrationBranch: 'main', releaseBranch: 'main',
          onIntegrationBranch: true, onReleaseBranch: true,
          detail: '4/4 on main.',
        },
      ],
    }));

    expect(fixture.componentInstance.label()).toBe(
      'agent-studio 5/5 develop and main · runner 4/4 main',
    );
    expect(fixture.componentInstance.tooltip()).toContain('runner: 4/4 on main.');
  });

  it('renders pending as amber "NICHT integriert" and flags acute', () => {
    const fixture = render(integration('pending'));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('NICHT integriert');
    expect(badge.dataset['kind']).toBe('pending');
    expect(badge.classList.contains('integration-badge--acute')).toBe(true);
  });

  it('renders partial as an orange "teilweise integriert" badge with missing SHAs in the tooltip', () => {
    const fixture = render(
      integration('partial', { detail: '1/2 attributed commits integrated; missing: beef123' }),
    );
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('teilweise integriert');
    expect(badge.dataset['kind']).toBe('partial');
    expect(badge.classList.contains('integration-badge--acute')).toBe(true);
    expect(fixture.componentInstance.tooltip()).toContain('Partially integrated');
    expect(fixture.componentInstance.tooltip()).toContain('beef123');
  });

  it('renders conflict-skipped as a hard red integration-failed badge', () => {
    const fixture = render(integration('conflict-skipped', {
      detail: 'No reviewed delivery branch exists.',
      failure: {
        code: 'no-task-branch',
        label: 'No task branch',
        reason: 'No reviewed delivery branch exists.',
        rebaseRecoveryAvailable: false,
      },
    }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('No task branch');
    expect(badge.dataset['kind']).toBe('conflict');
    expect(badge.classList.contains('integration-badge--acute')).toBe(true);
    expect(fixture.componentInstance.tooltip()).toContain('No reviewed delivery branch exists.');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-integration-recovery"]')).toBeNull();
  });

  it('renders a classified task-key failure on the card and hides the unrelated rebase action', () => {
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', integration('conflict-skipped', {
      detail: 'The task key could not be resolved while validating the reviewed delivery.',
      failure: {
        code: 'review-subject-task-key-unavailable',
        label: 'Task key unavailable',
        reason: 'The task key could not be resolved while validating the reviewed delivery.',
        rebaseRecoveryAvailable: false,
      },
    }));
    fixture.componentRef.setInput('jobId', 'task-1');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector(
      '[data-testid="integration-status-badge"]',
    ) as HTMLElement;
    expect(badge.textContent).toContain('Task key unavailable');
    expect(badge.dataset['integrationFailureCode']).toBe('review-subject-task-key-unavailable');
    expect(fixture.componentInstance.tooltip()).toContain('task key could not be resolved');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-recovery"]',
    )).toBeNull();
  });

  it('offers rebase recovery for the classified source-needs-rebase state', () => {
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', integration('conflict-skipped', {
      failure: {
        code: 'source-needs-rebase',
        label: 'Rebase required',
        reason: 'The reviewed delivery is behind the integration branch.',
        rebaseRecoveryAvailable: true,
      },
    }));
    fixture.componentRef.setInput('jobId', 'task-1');
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Rebase required');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-recovery"]',
    )).toBeTruthy();
  });

  it('queues a focused rebase steer round from a conflict card', () => {
    const tasks = TestBed.inject(TaskService);
    const refresh = vi.spyOn(tasks, 'refresh').mockImplementation(() => undefined);
    const notifications = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput(
      'integration',
      integration('conflict-skipped', { detail: 'Conflicted: shared.txt' }),
    );
    fixture.componentRef.setInput('jobId', 'task-1');
    fixture.componentRef.setInput('watchPath', '/tmp/watch');
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-recovery"]',
    ) as HTMLButtonElement;
    expect(button).toBeTruthy();
    button.click();
    expect(fixture.componentInstance.recoveryPending()).toBe(true);

    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne((req) =>
      req.method === 'POST'
      && req.url === '/api/tasks/task-1/integration/rebase'
      && req.params.get('watchPath') === '/tmp/watch',
    );
    expect(request.request.body).toBeNull();
    request.flush({
      status: 'queued',
      mode: 'steer',
      targetState: '2-ready',
      position: 0,
      deliveryRef: 'runner/agent-runner-01/AGT-2227',
      resultSha: 'a'.repeat(40),
      integrationBranch: 'develop',
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.recoveryPending()).toBe(false);
    expect(refresh).toHaveBeenCalledWith(true);
    expect(notifications.notifications().at(-1)?.message).toContain(
      'runner/agent-runner-01/AGT-2227',
    );
    http.verify();
  });

  it('renders no-branch as grey "kein Branch"', () => {
    const fixture = render(integration('no-branch'));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('kein Branch');
    expect(badge.dataset['kind']).toBe('no-branch');
    expect(badge.classList.contains('integration-badge--acute')).toBe(false);
  });

  it('hides when there is no integration verdict', () => {
    const fixture = render(null);
    expect(fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]')).toBeNull();
  });

  it('honours a custom integration branch in the label and tooltip', () => {
    const fixture = render(integration('pending', { integrationBranch: 'trunk' }));
    expect(fixture.componentInstance.tooltip()).toContain('NOT integrated into trunk');
    expect(fixture.componentInstance.ariaLabel()).toContain('Not integrated into trunk');
  });
});
