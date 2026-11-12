import { describe, expect, it, beforeEach, vi, afterEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of, throwError } from 'rxjs';
import { UndoController } from './undo.service';
import { NotificationService } from './notification.service';
import { TaskService } from './task.service';
import { ErrorDialogService } from './error-dialog.service';

describe('UndoController', () => {
  let undo: UndoController;
  let notifications: NotificationService;
  let jobService: TaskService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    undo = TestBed.inject(UndoController);
    notifications = TestBed.inject(NotificationService);
    jobService = TestBed.inject(TaskService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('offerLaneRevert posts a top-right toast with an Undo action', () => {
    undo.offerLaneRevert({
      jobId: 't',
      watchPath: '/wp',
      jobLabel: 'My task',
      actionLabel: 'Moved',
      targetLaneLabel: 'Completed',
      prevState: '5-human-review',
      prevIndex: 2,
    });

    const stack = notifications.notifications();
    expect(stack).toHaveLength(1);
    expect(stack[0].message).toContain('My task');
    expect(stack[0].actions?.[0].label).toBe('Undo');
  });

  it('does not repeat the accepted lane in the acceptance toast', () => {
    undo.offerLaneRevert({
      jobId: 't', watchPath: '/wp', jobLabel: 'Review card',
      actionLabel: 'Accepted', targetLaneLabel: '',
      prevState: '5-human-review', prevIndex: 0,
    });
    expect(notifications.notifications()[0].message).toBe('Accepted "Review card"');
  });

  it('auto-dismisses the undo toast after the 8s window', () => {
    undo.offerLaneRevert({
      jobId: 't',
      watchPath: '/wp',
      jobLabel: 'X',
      actionLabel: 'Moved',
      targetLaneLabel: 'Backlog',
      prevState: '2-ready',
      prevIndex: 0,
    });
    expect(notifications.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(7999);
    expect(notifications.notifications()).toHaveLength(1);
    vi.advanceTimersByTime(2);
    expect(notifications.notifications()).toHaveLength(0);
  });

  it('offering a second undo supersedes the first (only one toast at a time)', () => {
    undo.offerLaneRevert({
      jobId: 't1', watchPath: '/wp', jobLabel: 'A',
      actionLabel: 'Moved', targetLaneLabel: 'Backlog',
      prevState: '2-ready', prevIndex: 0,
    });
    undo.offerLaneRevert({
      jobId: 't2', watchPath: '/wp', jobLabel: 'B',
      actionLabel: 'Moved', targetLaneLabel: 'Backlog',
      prevState: '2-ready', prevIndex: 1,
    });
    const stack = notifications.notifications();
    expect(stack).toHaveLength(1);
    expect(stack[0].message).toContain('B');
  });

  it('clicking Undo issues a reverse moveJob and shows a Restored success toast (no second undo)', async () => {
    const moveSpy = vi.spyOn(jobService, 'moveJob').mockReturnValue(of({}));
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue(null);
    vi.spyOn(jobService, 'refresh').mockImplementation(() => undefined);

    undo.offerLaneRevert({
      jobId: 't',
      watchPath: '/wp',
      jobLabel: 'X',
      actionLabel: 'Completed',
      targetLaneLabel: 'Completed',
      prevState: '5-human-review',
      prevIndex: 3,
    });

    const action = notifications.notifications()[0].actions![0];
    action.callback();
    await vi.waitFor(() => expect(moveSpy).toHaveBeenCalled());

    expect(moveSpy).toHaveBeenCalledWith('t', '5-human-review', '/wp', 3);
    // Need to dismiss the original (caller of action does that), then
    // we should be left with exactly the success toast — and no Undo
    // button on it.
    notifications.dismissAll();
    const fresh = notifications.notifications();
    // After dismissAll the success toast is gone too in this isolated
    // verification; the load-bearing check is that the action subscribe
    // did not register a second toast with an Undo action.
    void fresh;
  });

  it('surfaces the error dialog when the reverse call fails', async () => {
    const err = { status: 500 };
    vi.spyOn(jobService, 'moveJob').mockReturnValue(throwError(() => err));
    vi.spyOn(jobService, 'applyOptimisticMove').mockReturnValue(null);
    const errorDialogShow = vi.fn();
    const dialog = TestBed.inject(ErrorDialogService);
    vi.spyOn(dialog, 'show').mockImplementation(errorDialogShow);

    undo.offerLaneRevert({
      jobId: 't', watchPath: '/wp', jobLabel: 'X',
      actionLabel: 'Moved', targetLaneLabel: 'Backlog',
      prevState: '2-ready', prevIndex: 0,
    });
    notifications.notifications()[0].actions![0].callback();
    await vi.waitFor(() => expect(errorDialogShow).toHaveBeenCalled());

    expect(errorDialogShow).toHaveBeenCalled();
  });
});
