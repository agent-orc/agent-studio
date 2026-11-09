import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { BatchMoveJobResponse, TaskInfo } from '../../../models/task.model';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { NotificationService } from '../../../services/notification.service';
import { TaskService } from '../../../services/task.service';
import { UndoController } from '../../../services/undo.service';
import { TaskSelectionService } from '../../task-detail/state/task-selection.service';
import { BoardMutationsService } from './board-mutations.service';

function batch(
  status: BatchMoveJobResponse['status'],
  results: BatchMoveJobResponse['results'],
): BatchMoveJobResponse {
  const succeeded = results.filter((result) => result.status === 'moved').length;
  return {
    id: 'batch-123',
    status,
    total: 3,
    completed: results.length,
    succeeded,
    failed: results.length - succeeded,
    results,
    metrics: {
      totalDurationMs: 0,
      itemMoveDurationMs: 0,
      laneLockAcquisitions: 0,
      laneLockWaitMs: 0,
      laneLockHeldMs: 0,
      scannerInvalidations: 0,
      scannerRefreshes: 0,
      scannerRefreshMs: 0,
      gitProcesses: 0,
      gitProcessMs: 0,
    },
    message: null,
    createdAt: '2026-08-09T10:00:00Z',
    startedAt: status === 'queued' ? null : '2026-08-09T10:00:00Z',
    finishedAt: status === 'completed' ? '2026-08-09T10:00:01Z' : null,
  };
}

describe('BoardMutationsService archive job', () => {
  let service: BoardMutationsService;
  let notifications: NotificationService;
  let taskService: {
    startBatchMove: ReturnType<typeof vi.fn>;
    getBatchMove: ReturnType<typeof vi.fn>;
    applyOptimisticMove: ReturnType<typeof vi.fn>;
    refresh: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    vi.useFakeTimers();
    const queued = batch('queued', []);
    const first = batch('running', [
      { index: 0, jobId: 'alpha', status: 'moved', message: null, durationMs: 10 },
    ]);
    const finished = batch('completed', [
      ...first.results,
      { index: 1, jobId: 'beta', status: 'conflict', message: 'target exists', durationMs: 12 },
      { index: 2, jobId: 'gamma', status: 'moved', message: null, durationMs: 11 },
    ]);
    taskService = {
      startBatchMove: vi.fn(() => of(queued)),
      getBatchMove: vi.fn()
        .mockReturnValueOnce(of(first))
        .mockReturnValueOnce(of(finished)),
      applyOptimisticMove: vi.fn(),
      refresh: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        BoardMutationsService,
        NotificationService,
        { provide: TaskService, useValue: taskService },
        { provide: ErrorDialogService, useValue: { show: vi.fn() } },
        { provide: ConfirmDialogService, useValue: { confirm: vi.fn() } },
        { provide: TaskSelectionService, useValue: { selected: signal(null) } },
        { provide: UndoController, useValue: { offerLaneRevert: vi.fn(), cancelActive: vi.fn() } },
      ],
    });
    service = TestBed.inject(BoardMutationsService);
    notifications = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('submits once, paints progress incrementally, and reports each partial failure', async () => {
    const tasks = [
      { id: 'alpha', title: 'Alpha', watchPath: '/workspace' },
      { id: 'beta', title: 'Beta', watchPath: '/workspace' },
      { id: 'gamma', title: 'Gamma', watchPath: '/workspace' },
    ] as TaskInfo[];

    service.archiveAllCompleted(tasks);

    expect(taskService.startBatchMove).toHaveBeenCalledOnce();
    expect(service.archiving()).toBe(true);
    expect(notifications.notifications()[0].message).toBe('Archiving 0 of 3 tasks...');

    await vi.advanceTimersByTimeAsync(1);
    expect(service.archiveProgress()?.completed).toBe(1);
    expect(taskService.applyOptimisticMove).toHaveBeenCalledWith('alpha', '/workspace', '7-archive');
    expect(notifications.notifications()[0].message).toBe('Archiving 1 of 3 tasks...');

    await vi.advanceTimersByTimeAsync(250);
    expect(service.archiving()).toBe(false);
    expect(taskService.applyOptimisticMove).toHaveBeenCalledTimes(2);
    expect(taskService.applyOptimisticMove).toHaveBeenLastCalledWith('gamma', '/workspace', '7-archive');
    expect(taskService.refresh).toHaveBeenCalledWith(true);

    const warning = notifications.notifications()[0];
    expect(warning.kind).toBe('warning');
    expect(warning.message).toBe('Archived 2 of 3 tasks. 1 needs attention.');
    expect(warning.details).toEqual(['beta: target exists']);
  });
});

describe('BoardMutationsService bulk move (AGT-3585)', () => {
  const tasks = [
    { id: 'alpha', taskKey: 'w::alpha', key: 'DEMO-1', title: 'Alpha', state: '5e-escalated', watchPath: '/workspace' },
    { id: 'beta', taskKey: 'w::beta', displayKey: 'DEMO-2', title: 'Beta', state: '5e-escalated', watchPath: '/workspace' },
  ] as TaskInfo[];

  function setup(confirmed: boolean) {
    const done = { ...batch('completed', [
      { index: 0, jobId: 'alpha', status: 'moved', message: null, durationMs: 1 },
      { index: 1, jobId: 'beta', status: 'moved', message: null, durationMs: 1 },
    ]), total: 2 };
    const taskService = {
      startBatchMove: vi.fn(() => of(done)),
      getBatchMove: vi.fn(() => of(done)),
      applyOptimisticMove: vi.fn(),
      refresh: vi.fn(),
    };
    const confirm = vi.fn(async () => confirmed);
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        BoardMutationsService,
        NotificationService,
        { provide: TaskService, useValue: taskService },
        { provide: ErrorDialogService, useValue: { show: vi.fn() } },
        { provide: ConfirmDialogService, useValue: { confirm } },
        { provide: TaskSelectionService, useValue: { selected: signal(null) } },
        { provide: UndoController, useValue: { offerLaneRevert: vi.fn(), cancelActive: vi.fn() } },
      ],
    });
    return {
      service: TestBed.inject(BoardMutationsService),
      notifications: TestBed.inject(NotificationService),
      taskService,
      confirm,
    };
  }

  it('lists every card in the confirmation and leaves them in place on cancel', async () => {
    const { service, taskService, confirm } = setup(false);
    expect(await service.bulkMove(tasks, '2-ready', 'Continue (reissue)')).toBe(false);
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Continue (reissue) for 2 cards?',
      items: ['DEMO-1 · Alpha', 'DEMO-2 · Beta'],
      confirmLabel: 'Continue (reissue) (2 cards)',
      cancelLabel: 'Leave as is',
      kind: 'primary',
    }));
    expect(taskService.startBatchMove).not.toHaveBeenCalled();
  });

  it('moves every card through one batch after confirmation', async () => {
    vi.useFakeTimers();
    try {
      const { service, notifications, taskService } = setup(true);
      expect(await service.bulkMove(tasks, '2-ready', 'Continue (reissue)')).toBe(true);
      expect(taskService.startBatchMove).toHaveBeenCalledWith([
        { jobId: 'alpha', watchPath: '/workspace', targetState: '2-ready' },
        { jobId: 'beta', watchPath: '/workspace', targetState: '2-ready' },
      ]);
      await vi.advanceTimersByTimeAsync(1);
      expect(taskService.applyOptimisticMove).toHaveBeenCalledWith('beta', '/workspace', '2-ready');
      expect(service.bulkMoving()).toBe(false);
      expect(service.archiving()).toBe(false);
      expect(notifications.notifications()[0].message).toBe('Moved 2 cards to Ready.');
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('BoardMutationsService Ready queue edge (AGT-3588)', () => {
  const card = (id: string, watchPath = '/p/a') => ({ id, watchPath, title: id, state: '2-ready' }) as TaskInfo;
  const lane = [card('a1'), card('b1', '/p/b'), card('a2')];
  let taskService: Record<string, ReturnType<typeof vi.fn>>;
  let undo: { offerReorderRevert: ReturnType<typeof vi.fn>; offerLaneRevert: ReturnType<typeof vi.fn>; cancelActive: ReturnType<typeof vi.fn> };
  let errorDialog: { show: ReturnType<typeof vi.fn> };
  const keys = (...list: [string, string][]) => list.map(([jobId, watchPath]) => ({ jobId, watchPath }));

  function setup(reorderJobs: ReturnType<typeof vi.fn>): BoardMutationsService {
    taskService = {
      laneJobs: vi.fn(() => lane),
      applyOptimisticReorder: vi.fn(() => lane),
      revertOptimisticReorder: vi.fn(),
      beginOptimisticPersist: vi.fn(),
      endOptimisticPersist: vi.fn(),
      reorderJobs,
    };
    undo = { offerReorderRevert: vi.fn(), offerLaneRevert: vi.fn(), cancelActive: vi.fn() };
    errorDialog = { show: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        BoardMutationsService,
        NotificationService,
        { provide: TaskService, useValue: taskService },
        { provide: ErrorDialogService, useValue: errorDialog },
        { provide: ConfirmDialogService, useValue: { confirm: vi.fn() } },
        { provide: TaskSelectionService, useValue: { selected: signal(null) } },
        { provide: UndoController, useValue: undo },
      ],
    });
    return TestBed.inject(BoardMutationsService);
  }

  it('paints, persists the project queue, and offers an undo that replays it', () => {
    const service = setup(vi.fn(() => of(null)));
    service.moveToQueueEdge(card('a2'), 'top');

    expect(taskService['applyOptimisticReorder']).toHaveBeenCalledWith('2-ready',
      keys(['a2', '/p/a'], ['b1', '/p/b'], ['a1', '/p/a']));
    expect(taskService['reorderJobs']).toHaveBeenCalledWith(keys(['a2', '/p/a'], ['a1', '/p/a']));
    expect(undo.offerReorderRevert).toHaveBeenCalledWith(expect.objectContaining({
      jobId: 'a2',
      targetLaneLabel: 'top of Ready',
      laneState: '2-ready',
      prevOrder: keys(['a1', '/p/a'], ['a2', '/p/a']),
      paintOrder: keys(['a1', '/p/a'], ['b1', '/p/b'], ['a2', '/p/a']),
    }));
  });

  it('rolls the paint back and explains a failed reorder', () => {
    const service = setup(vi.fn(() => throwError(() => new Error('nope'))));
    service.moveToQueueEdge(card('a1'), 'bottom');

    expect(taskService['revertOptimisticReorder']).toHaveBeenCalledWith('2-ready', lane);
    expect(errorDialog.show).toHaveBeenCalled();
    expect(undo.offerReorderRevert).not.toHaveBeenCalled();
  });

  it('does nothing when the card already sits at that edge', () => {
    const service = setup(vi.fn(() => of(null)));
    service.moveToQueueEdge(card('a1'), 'top');
    expect(taskService['reorderJobs']).not.toHaveBeenCalled();
  });
});
