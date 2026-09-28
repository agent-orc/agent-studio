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

  it('renders a code-free delivery as neutral and without a recovery action', () => {
    const fixture = render(integration('not-applicable'));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('No integration needed');
    expect(badge.dataset['kind']).toBe('no-branch');
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

  /**
   * AGT-2849: a merge only a local ref can see is not an integration. The badge
   * must not show the green "merged @sha" for it; it carries the amber
   * accepted-but-not-integrated treatment and says why.
   */
  it('renders merged-locally as an acute amber badge that names the missing push', () => {
    const fixture = render(integration('merged-locally', {
      detail: 'anchor-ancestor; merged into develop locally only - not reachable from origin/develop: beef123',
    }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;
    expect(badge.textContent).toContain('merged locally, not pushed');
    expect(badge.dataset['kind']).toBe('pending');
    expect(badge.dataset['integrationStatus']).toBe('merged-locally');
    expect(badge.classList.contains('integration-badge--acute')).toBe(true);
    expect(fixture.componentInstance.tooltip()).toContain('not reachable from origin/develop');
    expect(fixture.componentInstance.ariaLabel()).toBe(
      'Merged into develop locally but not pushed to origin/develop',
    );
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

  it('offers Retry integration on a gate-environment failure and reuses the passed review', () => {
    // AGT-2824: CAC-18 keeps this failure in `pending`, and the delivery itself
    // is fine, so the card must offer the cheap replay rather than the rebase
    // steer round that would re-review an unchanged delivery.
    const tasks = TestBed.inject(TaskService);
    const refresh = vi.spyOn(tasks, 'refresh').mockImplementation(() => undefined);
    const notifications = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', integration('pending', {
      detail: 'gate environment: The build gate blocked the merge into develop.',
      failure: {
        code: 'gate-environment-failure',
        label: 'Gate environment failure',
        reason: "Tool 'node' version v24.18.0 does not match .nvmrc",
        rebaseRecoveryAvailable: false,
      },
    }));
    fixture.componentRef.setInput('jobId', 'task-1');
    fixture.componentRef.setInput('watchPath', '/tmp/watch');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-recovery"]',
    )).toBeNull();
    const button = fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-retry"]',
    ) as HTMLButtonElement;
    expect(button).toBeTruthy();
    button.click();
    expect(fixture.componentInstance.retryPending()).toBe(true);

    const http = TestBed.inject(HttpTestingController);
    const request = http.expectOne((req) =>
      req.method === 'POST'
      && req.url === '/api/tasks/task-1/integration/retry'
      && req.params.get('watchPath') === '/tmp/watch',
    );
    expect(request.request.body).toBeNull();
    request.flush({
      status: 'integrated',
      reason: 'The delivery integrated without a new review round.',
      outcome: 'Merged',
      rung: 0,
      deliverySha: 'a'.repeat(40),
      integrationBranch: 'develop',
      reviewReused: true,
    });
    fixture.detectChanges();

    expect(fixture.componentInstance.retryPending()).toBe(false);
    expect(refresh).toHaveBeenCalledWith(true);
    expect(notifications.notifications().at(-1)?.message).toContain('without a new review');
    http.verify();
  });

  it('shows the policy reason when the server refuses the retry', () => {
    // AGT-2824 - the operator button overrides the ladder's timing, never its
    // eligibility, so a refusal must name the rule that refused it.
    const notifications = TestBed.inject(NotificationService);
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', integration('pending', {
      failure: {
        code: 'gate-environment-failure',
        label: 'Gate environment failure',
        reason: "Tool 'node' version v24.18.0 does not match .nvmrc",
        rebaseRecoveryAvailable: false,
      },
    }));
    fixture.componentRef.setInput('jobId', 'task-1');
    fixture.detectChanges();

    (fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-retry"]',
    ) as HTMLButtonElement).click();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/tasks/task-1/integration/retry').flush(
      {
        error: 'An acceptance integration for this task is already running; '
          + 'wait for it to settle before retrying.',
        status: 'not-applicable',
        code: 'acceptance-integration-in-flight',
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(fixture.componentInstance.retryPending()).toBe(false);
    expect(notifications.notifications().at(-1)?.message)
      .toContain('acceptance integration for this task is already running');
    http.verify();
  });

  it('does not offer Retry integration for any other integration failure', () => {
    const fixture = TestBed.createComponent(IntegrationStatusBadgeComponent);
    fixture.componentRef.setInput('integration', integration('conflict-skipped', {
      failure: {
        code: 'merge-conflict',
        label: 'Merge conflict',
        reason: 'The delivery conflicts with the current integration branch.',
        rebaseRecoveryAvailable: true,
      },
    }));
    fixture.componentRef.setInput('jobId', 'task-1');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-retry"]',
    )).toBeNull();
  });

  it('appends the requeue class to an infrastructure failure and its tooltip', () => {
    const fixture = render(integration('conflict-skipped', {
      detail: "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
      failure: {
        code: 'integration-error',
        label: 'Integration failed',
        reason: "Integration branch 'develop' could not be fetched from origin: git operation timed out after 30 seconds",
        rebaseRecoveryAvailable: false,
        failureClass: 'infrastructure',
        failureSignature: 'git-network-timeout',
      },
    }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;

    expect(badge.textContent).toContain('Integration failed · Infrastructure');
    expect(badge.dataset['integrationFailureClass']).toBe('infrastructure');
    expect(fixture.componentInstance.tooltip()).toContain('Infrastructure fault, will be retried automatically');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="task-card-integration-recovery"]',
    )).toBeNull();
  });

  it('appends the requeue class for a quota failure', () => {
    const fixture = render(integration('conflict-skipped', {
      failure: {
        code: 'integration-error',
        label: 'Integration failed',
        reason: 'The CLI provider quota is exhausted.',
        rebaseRecoveryAvailable: false,
        failureClass: 'quota',
        failureSignature: 'cli-quota-exhausted',
      },
    }));
    const badge = fixture.nativeElement.querySelector('[data-testid="integration-status-badge"]') as HTMLElement;

    expect(badge.textContent).toContain('Integration failed · Quota');
    expect(badge.dataset['integrationFailureClass']).toBe('quota');
  });

  it('does not append a requeue class for a product failure or a legacy record', () => {
    const productFixture = render(integration('conflict-skipped', {
      failure: {
        code: 'build-gate-failed',
        label: 'Build gate failed',
        reason: '1 test fails on the change that passes on the baseline.',
        rebaseRecoveryAvailable: false,
        failureClass: 'product',
        failureSignature: 'new-test-failures',
      },
    }));
    expect(productFixture.nativeElement.querySelector(
      '[data-testid="integration-status-badge"]',
    ).textContent).not.toContain('·');

    const legacyFixture = render(integration('conflict-skipped', {
      failure: {
        code: 'merge-conflict',
        label: 'Merge conflict',
        reason: 'The delivery conflicts with the current integration branch.',
        rebaseRecoveryAvailable: true,
      },
    }));
    expect(legacyFixture.nativeElement.querySelector(
      '[data-testid="integration-status-badge"]',
    ).textContent).not.toContain('·');
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
