import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { OnDemandPostStepAttempt, PostStepActivation } from '../../../../task-pipeline';
import { TaskPipelinePollService } from '../../../../polling/services/task-pipeline-poll.service';
import { StudioTabStateService } from '../../../../studio-shell/services/studio-tab-state.service';
import { NotificationService } from '../../../../../services/notification.service';
import { TaskService } from '../../../../../services/task.service';
import { PostStepControlsComponent } from './post-step-controls.component';

const ACTIVE: PostStepActivation = {
  state: 'active',
  source: 'global',
  reason: 'Enabled by the global catalogue default.',
};

function setup(options: {
  stepId?: string;
  activation?: PostStepActivation;
  attempts?: OnDemandPostStepAttempt[];
  reviewResults?: boolean;
} = {}) {
  const response = new Subject<{
    stepId: string;
    attempt: number;
    status: string;
    summary: string;
    artifactRef?: string | null;
  }>();
  const runTaskPostStep = vi.fn().mockReturnValue(response);
  const review = new Subject<{ stepId: string; ran: boolean; status: string | null; summary: string }>();
  const reviewTaskResults = vi.fn().mockReturnValue(review);
  const readJobFile = vi.fn();
  const refresh = vi.fn();
  const success = vi.fn();
  const warning = vi.fn();
  const open = vi.fn();

  TestBed.configureTestingModule({
    imports: [PostStepControlsComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { runTaskPostStep, reviewTaskResults, readJobFile } },
      { provide: TaskPipelinePollService, useValue: { refresh } },
      { provide: NotificationService, useValue: { success, warning } },
      { provide: StudioTabStateService, useValue: { open } },
    ],
  });

  const fixture = TestBed.createComponent(PostStepControlsComponent);
  fixture.componentRef.setInput('stepId', options.stepId ?? 'post-wiki-learnings');
  fixture.componentRef.setInput('label', 'Wiki learnings');
  fixture.componentRef.setInput('jobId', 'AGT-2116');
  fixture.componentRef.setInput('watchPath', 'C:/tasks/project');
  fixture.componentRef.setInput('projectName', 'Agent Studio');
  fixture.componentRef.setInput('activation', options.activation ?? ACTIVE);
  fixture.componentRef.setInput('attempts', options.attempts ?? []);
  fixture.componentRef.setInput('reviewResults', options.reviewResults ?? false);
  fixture.detectChanges();

  const root = fixture.nativeElement as HTMLElement;
  return {
    fixture,
    root,
    response,
    runTaskPostStep,
    review,
    reviewTaskResults,
    refresh,
    success,
    warning,
    open,
  };
}

describe('PostStepControlsComponent', () => {
  it('renders a compact scope code while preserving state, source and reason for assistive output', () => {
    const activation: PostStepActivation = {
      state: 'skipped',
      source: 'condition',
      reason: 'Condition "task has tag \'security\'" does not match this task run.',
    };
    const { root, open } = setup({ stepId: 'post-code-review-grade', activation });

    const source = root.querySelector('[data-testid="overview-post-step-source"]') as HTMLButtonElement;
    expect(source.textContent?.trim()).toBe('C');
    expect(source.dataset['activationState']).toBe('skipped');
    expect(source.dataset['activationSource']).toBe('condition');
    expect(source.getAttribute('aria-label')).toContain(activation.reason);

    source.click();
    expect(open).toHaveBeenCalledWith({
      kind: 'hub',
      projectName: 'Agent Studio',
      section: 'pipeline',
      pipelineStepId: 'post-code-review-grade',
    });
  });

  it.each([
    ['global', 'G'],
    ['project', 'P'],
    ['condition', 'C'],
  ] as const)('maps the %s source to the compact %s code', (source, code) => {
    const { root } = setup({ activation: { ...ACTIVE, source } });

    expect(root.querySelector('[data-testid="overview-post-step-source"]')?.textContent?.trim()).toBe(code);
  });

  it('uses the shared pending-button contract immediately and refreshes after success', () => {
    const { fixture, root, response, runTaskPostStep, refresh, success } = setup();
    const button = root.querySelector('[data-testid="overview-post-step-run"]') as HTMLButtonElement;

    button.click();
    fixture.detectChanges();

    expect(runTaskPostStep).toHaveBeenCalledWith(
      'AGT-2116',
      'post-wiki-learnings',
      'C:/tasks/project',
    );
    expect(button.disabled).toBe(true);
    expect(button.getAttribute('aria-busy')).toBe('true');
    expect(button.getAttribute('data-pending-label')).toBe('Running…');

    response.next({
      stepId: 'post-wiki-learnings',
      attempt: 2,
      status: 'Ok',
      summary: 'Learning refreshed.',
      artifactRef: 'results/post-steps/post-wiki-learnings-attempt-002.md',
    });
    response.complete();
    fixture.detectChanges();

    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-busy')).toBeNull();
    expect(refresh).toHaveBeenCalledOnce();
    expect(success).toHaveBeenCalledWith(
      'Wiki learnings attempt #2: Learning refreshed.',
      'Post-step finished',
    );
  });

  it('shows every matching attempt and its immutable artifact instead of only a count', () => {
    const attempts: OnDemandPostStepAttempt[] = [
      {
        stepId: 'post-wiki-learnings',
        attempt: 1,
        status: 'Ok',
        summary: 'First result.',
        startedAt: '2026-07-13T10:00:00Z',
        finishedAt: '2026-07-13T10:00:01Z',
        durationMs: 1000,
        artifactRef: 'results/post-steps/post-wiki-learnings-attempt-001.md',
      },
      {
        stepId: 'post-wiki-learnings',
        attempt: 2,
        status: 'Failed',
        summary: 'Second result.',
        startedAt: '2026-07-13T11:00:00Z',
        finishedAt: '2026-07-13T11:00:02Z',
        durationMs: 2000,
        artifactRef: 'results/post-steps/post-wiki-learnings-attempt-002.md',
      },
      {
        stepId: 'post-agents-wiki-sync',
        attempt: 9,
        status: 'Ok',
        summary: 'Different step.',
        startedAt: '2026-07-13T12:00:00Z',
        finishedAt: '2026-07-13T12:00:01Z',
        durationMs: 1000,
        artifactRef: 'results/post-steps/other.md',
      },
    ];
    const { fixture, root } = setup({ attempts });

    const history = root.querySelector('[data-testid="overview-post-step-attempts"]') as HTMLButtonElement;
    expect(history.textContent).toContain('2 attempts');
    history.click();
    fixture.detectChanges();

    const rows = Array.from(root.querySelectorAll('[data-testid="overview-post-step-attempt-row"]'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('#2');
    expect(rows[1].textContent).toContain('#1');
    const artifacts = Array.from(root.querySelectorAll('[data-testid="overview-post-step-artifact"]'))
      .map(element => element.textContent?.trim());
    expect(artifacts).toEqual([
      'results/post-steps/post-wiki-learnings-attempt-002.md',
      'results/post-steps/post-wiki-learnings-attempt-001.md',
    ]);
    expect(root.querySelectorAll('app-pipeline-step-result')).toHaveLength(2);
  });

  it('runs only the missing report review through Review results', () => {
    const { fixture, root, review, reviewTaskResults, runTaskPostStep, refresh, success } = setup({
      stepId: 'aspect-requirement-fit',
      reviewResults: true,
    });
    expect(root.querySelector('[data-testid="overview-post-step-run"]')).toBeNull();
    const button = root.querySelector('[data-testid="overview-post-step-review-results"]') as HTMLButtonElement;
    expect(button.textContent?.trim()).toBe('Review results');

    button.click();
    fixture.detectChanges();

    expect(reviewTaskResults).toHaveBeenCalledWith('AGT-2116', 'C:/tasks/project');
    expect(runTaskPostStep).not.toHaveBeenCalled();
    expect(button.getAttribute('data-pending-label')).toBe('Reviewing…');

    review.next({
      stepId: 'aspect-requirement-fit',
      ran: true,
      status: 'pass',
      summary: 'Requirement fit of the report: pass.',
    });
    review.complete();
    fixture.detectChanges();

    expect(refresh).toHaveBeenCalledOnce();
    expect(success).toHaveBeenCalledWith('Requirement fit of the report: pass.', 'Results reviewed');
  });

  it('offers no review run for an aspect the backend does not list as missing', () => {
    const { root } = setup({ stepId: 'aspect-requirement-fit' });

    expect(root.querySelector('[data-testid="overview-post-step-review-results"]')).toBeNull();
    expect(root.querySelector('[data-testid="overview-post-step-run"]')).toBeNull();
  });

  it('keeps activation visible for post-steps without an on-demand runner', () => {
    const { root } = setup({ stepId: 'post-orchestrator-decision' });

    expect(root.querySelector('[data-testid="overview-post-step-source"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="overview-post-step-run"]')).toBeNull();
  });
});
