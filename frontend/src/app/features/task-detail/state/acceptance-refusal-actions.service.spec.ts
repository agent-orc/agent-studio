import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import type { TaskDetail, TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import { AcceptanceReadinessStore } from '../../acceptance-readiness';
import { StudioTabStateService } from '../../studio-shell/services/studio-tab-state.service';
import { TaskSelectionService } from './task-selection.service';
import { AcceptanceRefusalStore } from './acceptance-refusal.model';
import { AcceptanceRefusalActions } from './acceptance-refusal-actions.service';

const SUMMARY = 'The 830-card first-paint target remains unmet and lacks idle-host confirmation';
const CARD = {
  id: 'agt-3534', taskKey: 'wp::agt-3534', key: 'AGT-3534', state: '5-human-review', watchPath: '/wp',
  integration: { status: 'integrated', integrationBranch: 'develop' },
  reviewProjection: { attempts: [{ aspects: [
    { aspect: 'requirement-fit', status: 'concerns', classification: 'product', summary: SUMMARY },
  ] }] },
} as unknown as TaskInfo;
const CONTENT_GAP = { status: 409, error: { code: 'completion-content-gap',
  error: 'Completion refused: requirement-fit verdict was concerns.' } };

describe('AcceptanceRefusalActions (AGT-3873)', () => {
  let actions: AcceptanceRefusalActions;
  let tasks: TaskService;
  let selection: TaskSelectionService;
  let notify: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    actions = TestBed.inject(AcceptanceRefusalActions);
    tasks = TestBed.inject(TaskService);
    selection = TestBed.inject(TaskSelectionService);
    vi.spyOn(tasks, 'refresh').mockImplementation(() => undefined);
    vi.spyOn(TestBed.inject(AcceptanceReadinessStore), 'checkCard').mockResolvedValue();
    notify = vi.spyOn(TestBed.inject(NotificationService), 'notify');
  });
  afterEach(() => TestBed.resetTestingModule());

  it('opens a refused board card with its written-reason form requested', () => {
    const open = vi.spyOn(selection, 'openDetail').mockImplementation(() => undefined);
    const tab = vi.spyOn(TestBed.inject(StudioTabStateService), 'open');

    expect(actions.acceptRefusedOnBoard(CARD, CONTENT_GAP)).toBe(true);

    expect(tab).toHaveBeenCalledWith({ kind: 'task', taskKey: CARD.taskKey });
    expect(open).toHaveBeenCalledWith(CARD);
    expect(TestBed.inject(AcceptanceRefusalStore).overrideRequest()).toBe(CARD.taskKey);
    expect(TestBed.inject(AcceptanceRefusalStore).forTask(CARD.taskKey)?.code).toBe('completion-content-gap');
    const toast = notify.mock.calls[0][0];
    expect(toast).toMatchObject({ kind: 'warning', title: 'Accept refused', position: 'below-toolbar',
      message: 'Acceptance refused: requirement-fit verdict was concerns.',
      details: [`The review's concern: ${SUMMARY}`] });
    expect(toast.actions.map((action: { label: string }) => action.label))
      .toEqual(['Accept with a written reason', 'Review again']);
  });

  it('leaves a refusal no override can admit to the caller', () => {
    const open = vi.spyOn(selection, 'openDetail').mockImplementation(() => undefined);
    const missing = { status: 409, error: { code: 'completion-content-gap',
      error: 'Completion refused: the required report is missing (results/report.md).' } };
    expect(actions.acceptRefusedOnBoard(CARD, missing)).toBe(false);
    expect(actions.acceptRefusedOnBoard(CARD, { status: 409, error: { code: 'integration-dead-end', error: 'x' } }))
      .toBe(false);
    expect(actions.acceptRefusedOnBoard(CARD, { status: 500, error: {} })).toBe(false);
    expect(open).not.toHaveBeenCalled();
    expect(notify).not.toHaveBeenCalled();
  });

  it('does not reopen the card that already shows', () => {
    selection.selected.set({ info: CARD } as TaskDetail);
    const open = vi.spyOn(selection, 'openDetail').mockImplementation(() => undefined);
    actions.openOverride(CARD);
    expect(open).not.toHaveBeenCalled();
    expect(TestBed.inject(AcceptanceRefusalStore).overrideRequest()).toBe(CARD.taskKey);
  });

  it('runs the toast actions: the override form and a review against the current brief', () => {
    selection.selected.set({ info: CARD } as TaskDetail);
    const review = vi.spyOn(tasks, 'reviewContentAgainstCurrentBrief').mockReturnValue(of({ reviewAttemptId: 'review-2', briefVersion: 'b2' }));
    const store = TestBed.inject(AcceptanceRefusalStore);
    actions.report(CARD, { taskKey: CARD.taskKey, code: 'completion-content-gap',
      message: 'Acceptance refused: requirement-fit verdict was concerns.' });
    const [override, again] = notify.mock.calls[0][0].actions as { callback: () => void }[];

    override.callback();
    expect(store.overrideRequest()).toBe(CARD.taskKey);
    again.callback();
    expect(review).toHaveBeenCalledWith('agt-3534', '/wp');
    expect(store.forTask(CARD.taskKey)).toBeNull();
  });

  it('names a failed review request', () => {
    vi.spyOn(tasks, 'reviewContentAgainstCurrentBrief').mockReturnValue(throwError(() => ({
      status: 409, error: { error: 'A review is already running.' } })));
    const error = vi.spyOn(TestBed.inject(NotificationService), 'error');
    actions.reviewAgain(CARD);
    expect(error).toHaveBeenCalledWith('A review is already running.', 'Review again failed');
  });
});
