import { describe, expect, it, vi, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { Subject, of, throwError } from 'rxjs';
import { TriageController } from './triage-controller.service';
import { TaskSelectionService } from './task-selection.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import { LanePagerService } from './lane-pager.service';
import { TaskService } from '../../../services/task.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import type { TaskInfo, TaskDetail } from '../../../models/task.model';

const noop = (): void => undefined;

/**
 * Regression for orchestrator-decision-closing-task:
 *
 * When the orchestrator moves a task from one lane to another (e.g.
 * 4-auto-review -> 5-human-review), the auto-advance effect fires
 * because the selected job's state diverges from triageLaneState.
 * If no peers remain in the original lane, the old code called
 * closeDetail() and the task panel closed from under the user.
 *
 * The fix: when `external === true` and no candidate peer exists,
 * follow the job to its new state instead of closing the panel.
 */
describe('TriageController · advanceToNextInLane', () => {
  let ctrl: TriageController;
  let selection: TaskSelectionService;
  let closedDetail: boolean;

  const makeJob = (id: string, state: string): TaskInfo =>
    ({
      id,
      taskKey: `wp::${id}`,
      title: id,
      state,
      order: 1,
      watchPath: '/wp',
      projectName: 'p',
    }) as unknown as TaskInfo;

  beforeEach(async () => {
    closedDetail = false;

    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    ctrl = TestBed.inject(TriageController);
    selection = TestBed.inject(TaskSelectionService);

    vi.spyOn(selection, 'closeDetail').mockImplementation(() => {
      closedDetail = true;
    });
    vi.spyOn(selection, 'showTriageToast').mockImplementation(noop);
  });

  it('closes the panel on an internal (user-initiated) lane-clear', () => {
    const job = makeJob('task-a', '4-auto-review');
    selection.triageLaneState = '4-auto-review';

    ctrl.advanceToNextInLane('4-auto-review', job.taskKey, [job], false);

    expect(closedDetail).toBe(true);
  });

  it('does NOT close the panel on an external move when the lane is empty', () => {
    const job = makeJob('task-a', '5-human-review');
    selection.triageLaneState = '4-auto-review';
    (selection as unknown as { selected: ReturnType<typeof signal<TaskDetail | null>> }).selected = signal<TaskDetail | null>({
      info: job,
    } as unknown as TaskDetail);

    ctrl.advanceToNextInLane('4-auto-review', job.taskKey, [job], true);

    expect(closedDetail).toBe(false);
    expect(selection.triageLaneState).toBe('5-human-review');
  });
});

/**
 * The "accept-to-next-task feels instant" regression: when the user
 * clicks Mark-as-Done on Job A, the next peer must render before the
 * move POST returns. The previous shape awaited the POST first, so the
 * user paid the POST roundtrip plus a getDetail roundtrip in series.
 *
 * The two assertions below pin the new contract:
 *   1. The panel advances synchronously - by the time `move(...)`
 *      returns, the prefetched detail for the next peer is in
 *      `selected`, before any `next` callback on the POST observable
 *      fires.
 *   2. When the POST eventually errors, the optimistic move is
 *      reverted and the panel navigates back to the original job, so
 *      the user does not silently lose their click.
 */
describe('TriageController · optimistic navigation on Accept', () => {
  let ctrl: TriageController;
  let selection: TaskSelectionService;
  let prefetch: TaskDetailPrefetchService;
  let jobService: TaskService;

  const wp = '/wp';
  const makeJob = (id: string, state: string): TaskInfo =>
    ({
      id,
      taskKey: `${wp}::${id}`,
      title: id,
      state,
      order: 1,
      watchPath: wp,
      projectName: 'p',
    }) as unknown as TaskInfo;

  const makeDetail = (id: string, state: string): TaskDetail =>
    ({
      info: makeJob(id, state),
      promptMarkdown: null,
      promptHistory: [],
      titleHistory: [],
      statusMarkdown: null,
      contextUsage: null,
      log: [],
      summaryState: null,
      reviewEvidence: [],
    }) as unknown as TaskDetail;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    ctrl = TestBed.inject(TriageController);
    selection = TestBed.inject(TaskSelectionService);
    prefetch = TestBed.inject(TaskDetailPrefetchService);
    jobService = TestBed.inject(TaskService);
    prefetch.clear();
  });

  it('advances synchronously to the prefetched next peer while the move POST is still in flight', () => {
    const taskA = makeJob('task-a', '5-human-review');
    const taskB = makeJob('task-b', '5-human-review');
    const taskBDetail = makeDetail('task-b', '5-human-review');

    // Seed the lane-pager snapshot for [A, B], anchored on A. The
    // service's effect prefetches B on snapshot change, so we mock the
    // GET to land deterministically before move() runs.
    const detailSpy = vi.spyOn(jobService, 'getDetail').mockReturnValue(of(taskBDetail));
    TestBed.inject(LanePagerService).capture('5-human-review', [taskA, taskB], taskA.taskKey);
    selection.triageLaneState = '5-human-review';
    (selection as unknown as { selected: ReturnType<typeof signal<TaskDetail | null>> }).selected
      = signal<TaskDetail | null>(makeDetail('task-a', '5-human-review'));

    // POST never resolves so we can assert nav happened before completion.
    const movePost = new Subject<object>();
    vi.spyOn(jobService, 'moveJob').mockReturnValue(movePost.asObservable());
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue({} as never);

    ctrl.move(taskA, { targetState: '6-completed', actionId: 'mark-done' });
    const observedSelectedDuringCall: TaskDetail | null = selection.selected();

    expect(observedSelectedDuringCall?.info.id).toBe('task-b');
    // POST is still on the wire — no `next` callback has fired yet.
    expect(detailSpy).toHaveBeenCalled();
    movePost.complete();
  });

  it('reverts the optimistic move and navigates back when the POST errors', () => {
    const taskA = makeJob('task-a', '5-human-review');
    const taskB = makeJob('task-b', '5-human-review');
    const taskADetail = makeDetail('task-a', '5-human-review');
    const taskBDetail = makeDetail('task-b', '5-human-review');

    vi.spyOn(jobService, 'getDetail').mockImplementation((id: string) => {
      return of(id === 'task-a' ? taskADetail : taskBDetail);
    });
    TestBed.inject(LanePagerService).capture('5-human-review', [taskA, taskB], taskA.taskKey);
    selection.triageLaneState = '5-human-review';
    (selection as unknown as { selected: ReturnType<typeof signal<TaskDetail | null>> }).selected
      = signal<TaskDetail | null>(taskADetail);

    const revertSpy = vi.spyOn(jobService, 'revertOptimisticMove').mockImplementation(noop);
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue(
      { fromLane: 'humanReview', before: [], toLane: 'completed', toBefore: [] } as never,
    );
    vi.spyOn(jobService, 'moveJob').mockReturnValue(
      throwError(() => ({ status: 500, message: 'boom' })),
    );

    // Suppress the error dialog so the test does not pop a modal.
    const errorDialog = TestBed.inject(ErrorDialogService);
    vi.spyOn(errorDialog, 'show').mockImplementation(noop);

    const openSpy = vi.spyOn(selection, 'openDetail').mockImplementation(noop);

    ctrl.move(taskA, { targetState: '6-completed', actionId: 'mark-done' });

    expect(revertSpy).toHaveBeenCalled();
    expect(openSpy).toHaveBeenCalledWith(taskA);
  });
});

/**
 * AGT-2069 — the planning-task spawn-contract accept guard (the AGT-1915 trap).
 *
 * Accepting a planning task into 6-completed must first pass through the
 * spawn-contract confirm dialog when the task spawned no follow-up cards and
 * carries no "no follow-up intended" declaration. The operator can override, but
 * never by accident. Coding tasks and contract-satisfied planning tasks move
 * straight through with no dialog.
 */
describe('TriageController · planning accept spawn-contract guard', () => {
  let ctrl: TriageController;
  let jobService: TaskService;
  let selection: TaskSelectionService;
  let confirmDialog: ConfirmDialogService;

  const wp = '/wp';

  const makePlanningJob = (contractSatisfied: boolean): TaskInfo =>
    ({
      id: 'plan-a',
      taskKey: `${wp}::plan-a`,
      title: 'Plan A',
      state: '5-human-review',
      order: 1,
      watchPath: wp,
      projectName: 'p',
      mode: 'planning',
      planningSpawn: {
        spawned: [],
        spawnedCount: 0,
        // A satisfied contract in this fixture comes from a deliberate
        // no-follow-up declaration; an unsatisfied one has neither spawns
        // nor declaration.
        noFollowUpDeclared: contractSatisfied,
        contractSatisfied,
      },
    }) as unknown as TaskInfo;

  const makeCodingJob = (): TaskInfo =>
    ({
      id: 'code-a',
      taskKey: `${wp}::code-a`,
      title: 'Code A',
      state: '5-human-review',
      order: 1,
      watchPath: wp,
      projectName: 'p',
      mode: 'coding',
    }) as unknown as TaskInfo;

  const makeCompletedJob = (status: 'integrated' | 'pending' | 'no-branch' | null): TaskInfo =>
    ({
      id: 'done-a',
      taskKey: `${wp}::done-a`,
      title: 'Done A',
      state: '6-completed',
      order: 1,
      watchPath: wp,
      projectName: 'p',
      mode: 'coding',
      integration: status === null ? null : {
        status,
        deliveryRef: 'task/triage-fixture',
        sha: status === 'integrated' ? 'abc1234' : null,
        integrationBranch: 'develop',
        detail: status === 'integrated' ? 'Already integrated.' : 'Merge is still pending.',
      },
    }) as unknown as TaskInfo;

  // confirmPlanningAcceptThenMove awaits the confirm promise, so let queued
  // microtasks/macrotasks drain before asserting.
  const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    ctrl = TestBed.inject(TriageController);
    jobService = TestBed.inject(TaskService);
    selection = TestBed.inject(TaskSelectionService);
    confirmDialog = TestBed.inject(ConfirmDialogService);

    // Neutralise the optimistic-move plumbing so whether performMove ran is
    // observable purely through moveJob().
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue({} as never);
    vi.spyOn(jobService, 'findLaneIndex').mockReturnValue(-1);
    vi.spyOn(jobService, 'moveJob').mockReturnValue(of({}));
    // AGT-2817: the archive guard resolves an absent verdict through the
    // per-card containment answer before it decides whether to speak.
    vi.spyOn(jobService, 'getDeliveryClaim').mockReturnValue(of({
      containmentStatus: 'integrated',
      integrated: true,
      deliveryRef: 'task/triage-fixture',
      integrationBranch: 'develop',
    }) as never);
    vi.spyOn(selection, 'advanceAfterMutation').mockReturnValue(true);
    vi.spyOn(selection, 'triageLanePeers').mockReturnValue([]);
  });

  it('pops the warning and does NOT move when the operator cancels', async () => {
    const job = makePlanningJob(false);
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(false);

    ctrl.move(job, { targetState: '6-completed', actionId: 'mark-done' });
    await flush();

    expect(confirmSpy).toHaveBeenCalledTimes(1);
    const opts = confirmSpy.mock.calls[0][0];
    expect(opts.kind).toBe('danger');
    expect(String(opts.title)).toMatch(/planning/i);
    expect(jobService.moveJob).not.toHaveBeenCalled();
  });

  it('moves when the operator confirms "Accept anyway"', async () => {
    const job = makePlanningJob(false);
    vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '6-completed', actionId: 'mark-done' });
    await flush();

    expect(jobService.moveJob).toHaveBeenCalledWith('plan-a', '6-completed', wp, undefined, undefined);
  });

  it('moves a contract-satisfied planning task straight through with no warning', async () => {
    const job = makePlanningJob(true);
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '6-completed', actionId: 'mark-done' });
    await flush();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(jobService.moveJob).toHaveBeenCalledWith('plan-a', '6-completed', wp, undefined, undefined);
  });

  it('never gates a coding task', async () => {
    const job = makeCodingJob();
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '6-completed', actionId: 'mark-done' });
    await flush();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(jobService.moveJob).toHaveBeenCalled();
  });

  it('names what is lost and what the archive keeps for a genuinely unintegrated delivery', async () => {
    const job = makeCompletedJob('pending');
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(false);

    ctrl.move(job, { targetState: '7-archive', actionId: 'archive' });
    await flush();

    expect(confirmSpy).toHaveBeenCalledTimes(1);
    const options = confirmSpy.mock.calls[0][0];
    expect(options.confirmLabel).toBe('Archive anyway');
    expect(String(options.message)).toContain('task/triage-fixture');
    expect(String(options.message)).toContain('develop');
    expect(String(options.message)).toContain('keeps the task and all of its evidence');
    // "pending" is only honest when something is genuinely waiting.
    expect(String(options.message)).not.toContain('status: pending');
    expect(jobService.moveJob).not.toHaveBeenCalled();
  });

  it('records the archive reason on the move when the operator closes anyway', async () => {
    const job = makeCompletedJob('pending');
    vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '7-archive', actionId: 'archive' });
    await flush();

    const call = (jobService.moveJob as unknown as { mock: { calls: unknown[][] } }).mock.calls[0];
    expect(call[0]).toBe('done-a');
    expect(call[1]).toBe('7-archive');
    expect(String(call[4])).toContain('task/triage-fixture');
  });

  /**
   * AGT-2706: the card carried no integration projection while its delivery
   * was contained in develop and in main. The guard now resolves the absence
   * instead of reporting it as a pending integration, so no dialog appears.
   */
  it('resolves an absent verdict through containment instead of warning', async () => {
    const job = makeCompletedJob(null);
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '7-archive', actionId: 'archive' });
    await flush();

    expect(jobService.getDeliveryClaim).toHaveBeenCalledWith('done-a', wp);
    expect(confirmSpy).not.toHaveBeenCalled();
    expect(jobService.moveJob).toHaveBeenCalledWith('done-a', '7-archive', wp, undefined, undefined);
  });

  it('archives an integrated Delivered task without a warning or a lookup', async () => {
    const job = makeCompletedJob('integrated');
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '7-archive', actionId: 'archive' });
    await flush();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(jobService.getDeliveryClaim).not.toHaveBeenCalled();
    expect(jobService.moveJob).toHaveBeenCalledWith('done-a', '7-archive', wp, undefined, undefined);
  });

  it('does not dialog when the card has nothing to integrate', async () => {
    const job = makeCompletedJob('no-branch');
    const confirmSpy = vi.spyOn(confirmDialog, 'confirm').mockResolvedValue(true);

    ctrl.move(job, { targetState: '7-archive', actionId: 'archive' });
    await flush();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(jobService.moveJob).toHaveBeenCalled();
  });
});
