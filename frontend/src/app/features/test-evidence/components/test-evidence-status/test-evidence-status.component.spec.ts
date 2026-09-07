import { TestBed } from '@angular/core/testing';
import { TaskState, type TaskInfo, type TaskTestRunEvidence } from '../../../../models/task.model';
import { TestEvidenceStatusComponent, visibleTestEvidence } from './test-evidence-status.component';

const missingEvidence: TaskTestRunEvidence = {
  runId: null,
  runCommit: null,
  runState: null,
  runResult: null,
  matchQuality: 'none',
  direction: 'none',
  distance: null,
  diffContained: false,
  evidenceState: 'unassigned',
  awaitingEvidence: false,
  summary: 'No test evidence assigned: card has no commit',
};

function task(state: string, overrides: Partial<TaskInfo> = {}): TaskInfo {
  return {
    state,
    commit: null,
    commits: [],
    integration: null,
    testEvidence: missingEvidence,
    ...overrides,
  } as TaskInfo;
}

describe('TestEvidenceStatusComponent', () => {
  it.each([
    TaskState.Backlog,
    TaskState.Preparation,
    TaskState.OrchestratorPrep,
    TaskState.Ready,
    TaskState.Progress,
    TaskState.FailedPickup,
    TaskState.CodeNotComplete,
  ])('suppresses missing evidence in %s before a delivery exists', (state) => {
    expect(visibleTestEvidence(task(state))).toBeNull();
  });

  it.each([
    TaskState.AutoReview,
    TaskState.HumanReview,
    TaskState.Completed,
    TaskState.Archive,
  ])('shows missing evidence after the task reaches %s', (state) => {
    expect(visibleTestEvidence(task(state))).toBe(missingEvidence);
  });

  it.each([
    ['attributed commits', { commits: [{ sha: 'abcdef12' }] }],
    ['a legacy attributed commit', { commit: { sha: 'abcdef12' } }],
    ['an attributed delivery ref', { integration: { deliveryRef: 'runner/host/TASK-1' } }],
  ] as const)('shows missing evidence in Ready when the task has %s', (_label, delivery) => {
    expect(visibleTestEvidence(task(TaskState.Ready, delivery as Partial<TaskInfo>))).toBe(missingEvidence);
  });

  it('keeps recorded evidence visible before review', () => {
    const recordedEvidence: TaskTestRunEvidence = {
      ...missingEvidence,
      runId: 'TR-42',
      runCommit: 'abcdef12',
      runState: 'completed',
      runResult: 'passed',
      matchQuality: 'perfect',
      direction: 'exact',
      distance: 0,
      diffContained: true,
      evidenceState: 'proven',
      summary: 'Perfect match',
    };

    expect(visibleTestEvidence(task(TaskState.Ready, { testEvidence: recordedEvidence })))
      .toBe(recordedEvidence);
  });

  it.each([
    ['perfect', 'proven', 'Perfect match'],
    ['contains-diff', 'proven', '10 commit(s) after, diff included'],
    ['none', 'unassigned', 'No matching test run'],
  ] as const)('renders %s evidence honestly', async (quality, state, summary) => {
    await TestBed.configureTestingModule({ imports: [TestEvidenceStatusComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TestEvidenceStatusComponent);
    const evidence = {
      ...missingEvidence,
      runId: quality === 'none' ? null : 'TR-42',
      runCommit: quality === 'none' ? null : 'abcdef12',
      runState: quality === 'none' ? null : 'completed',
      runResult: quality === 'none' ? null : 'passed',
      matchQuality: quality,
      direction: quality === 'perfect' ? 'exact' : quality === 'contains-diff' ? 'after' : 'none',
      distance: quality === 'perfect' ? 0 : quality === 'contains-diff' ? 10 : null,
      diffContained: quality !== 'none',
      evidenceState: state,
      summary,
    } satisfies TaskTestRunEvidence;
    fixture.componentRef.setInput('task', task(TaskState.HumanReview, { testEvidence: evidence }));
    fixture.detectChanges();

    const element = fixture.nativeElement.querySelector('[data-testid="task-card-test-evidence"]') as HTMLElement;
    expect(element.textContent).toContain(summary);
    expect(element.getAttribute('data-match-quality')).toBe(quality);
    expect(element.getAttribute('data-evidence-state')).toBe(state);
    if (quality === 'none') expect(element.textContent).toContain('No SHA-linked project run');
  });

  it('removes the missing-evidence block from the DOM for a Ready task without delivery', async () => {
    await TestBed.configureTestingModule({ imports: [TestEvidenceStatusComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TestEvidenceStatusComponent);
    fixture.componentRef.setInput('task', task(TaskState.Ready));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-test-evidence"]')).toBeNull();
  });

  it.each([
    ['not-applicable', 'No build/test defined', 'not-applicable'],
    ['not-proven', 'Build/test gate skipped at d1649ce9', 'not-proven'],
  ] as const)('keeps build gate state %s distinct on the card', async (state, summary, sourceResult) => {
    await TestBed.configureTestingModule({ imports: [TestEvidenceStatusComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TestEvidenceStatusComponent);
    const evidence = {
      ...missingEvidence,
      matchQuality: 'perfect',
      direction: 'exact',
      distance: 0,
      diffContained: true,
      evidenceState: state,
      summary,
      sources: [{
        kind: 'build-test-gate',
        id: 'gate-42',
        commit: 'd1649ce9',
        result: sourceResult,
        observedAt: '2026-08-08T10:00:00Z',
        summary,
        reason: state === 'not-proven' ? 'The build command did not run.' : 'No build/test commands are defined.',
        reportRef: 'post-steps/build-test-gate-1.log',
      }],
    } satisfies TaskTestRunEvidence;
    fixture.componentRef.setInput('task', task(TaskState.HumanReview, { testEvidence: evidence }));
    fixture.detectChanges();

    const element = fixture.nativeElement.querySelector('[data-testid="task-card-test-evidence"]') as HTMLElement;
    expect(element.getAttribute('data-evidence-state')).toBe(state);
    expect(element.textContent).toContain(summary);
    expect(element.textContent).toContain('Build/test gate · gate-42');
  });

  it('names SHA-linked review and gate evidence instead of the unassigned default', async () => {
    await TestBed.configureTestingModule({ imports: [TestEvidenceStatusComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TestEvidenceStatusComponent);
    const evidence = {
      ...missingEvidence,
      matchQuality: 'perfect',
      direction: 'exact',
      distance: 0,
      diffContained: true,
      evidenceState: 'proven',
      summary: 'Review build-tests Pass at d1649ce9',
      sources: [
        {
          kind: 'review-build-tests',
          id: 'review-42',
          commit: 'd1649ce9',
          result: 'passed',
          observedAt: '2026-07-29T20:41:22Z',
          summary: 'Review build-tests Pass at d1649ce9',
          reason: 'verify-1 and verify-2 passed.',
          reportRef: 'remote-review-grade-review-42.md',
        },
        {
          kind: 'build-test-gate',
          id: 'gate-42',
          commit: 'd1649ce9',
          result: 'passed',
          observedAt: '2026-07-29T20:40:00Z',
          summary: 'Build/test gate green at d1649ce9',
          reason: 'All selected commands passed.',
          reportRef: 'post-steps/build-test-gate-1.log',
        },
      ],
    } satisfies TaskTestRunEvidence;
    fixture.componentRef.setInput('task', task(TaskState.HumanReview, { testEvidence: evidence }));
    fixture.detectChanges();

    const element = fixture.nativeElement.querySelector('[data-testid="task-card-test-evidence"]') as HTMLElement;
    expect(element.textContent).toContain('Review build-tests Pass at d1649ce9');
    expect(element.textContent).toContain('Build/test gate green at d1649ce9');
    expect(element.textContent).not.toContain('Evidence pending');
  });

  it('renders independent build and blocked-aspect rows with reasons and report links', async () => {
    await TestBed.configureTestingModule({ imports: [TestEvidenceStatusComponent] }).compileComponents();
    const fixture = TestBed.createComponent(TestEvidenceStatusComponent);
    const reportRef = 'remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md';
    const evidence = {
      ...missingEvidence,
      matchQuality: 'perfect',
      direction: 'exact',
      distance: 0,
      diffContained: true,
      evidenceState: 'proven',
      summary: 'Review build-tests Pass at 491ddd64 (verify-1, verify-2)',
      sources: [
        {
          kind: 'review-build-tests',
          id: 'review_ad5cca8e3178425fb9ba9cabe329d50e',
          commit: '491ddd64',
          result: 'passed',
          observedAt: '2026-08-31T12:00:00Z',
          summary: 'Review build-tests Pass at 491ddd64 (verify-1, verify-2)',
          reason: 'verify-1 and verify-2 passed.',
          reportRef,
        },
        {
          kind: 'review-aspects',
          id: 'review_ad5cca8e3178425fb9ba9cabe329d50e',
          commit: '491ddd64',
          result: 'blocked',
          observedAt: '2026-08-31T12:00:00Z',
          summary: 'Review blocked by documentation-impact',
          reason: 'documentation-impact blocked: Public API and state-file contract changed without corresponding load-bearing doc updates.',
          reportRef,
        },
      ],
    } satisfies TaskTestRunEvidence;
    fixture.componentRef.setInput('variant', 'panel');
    fixture.componentRef.setInput('task', task(TaskState.HumanReview, {
      id: 'AGT-2689',
      watchPath: '/work/demo',
      testEvidence: evidence,
    }));
    fixture.detectChanges();

    const build = fixture.nativeElement.querySelector('[data-testid="test-evidence-source-review-build-tests"]') as HTMLElement;
    const aspects = fixture.nativeElement.querySelector('[data-testid="test-evidence-source-review-aspects"]') as HTMLElement;
    expect(build.dataset['sourceResult']).toBe('passed');
    expect(build.textContent).toContain('verify-1 and verify-2 passed.');
    expect(aspects.dataset['sourceResult']).toBe('blocked');
    expect(aspects.textContent).toContain('documentation-impact blocked');
    expect(aspects.getAttribute('aria-label')).toContain('Public API and state-file contract changed');
    const links = fixture.nativeElement.querySelectorAll('a.test-evidence-source__report') as NodeListOf<HTMLAnchorElement>;
    expect(links).toHaveLength(2);
    expect(links[0].getAttribute('href')).toBe(
      `/api/tasks/AGT-2689/files/${reportRef}?watchPath=%2Fwork%2Fdemo&scope=workspace`,
    );
  });
});
