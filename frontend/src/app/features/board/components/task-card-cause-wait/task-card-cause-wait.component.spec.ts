import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { TaskSelectionService } from '../../../task-detail/runtime';
import { TaskCardCauseWaitComponent } from './task-card-cause-wait.component';
import { TaskState, type TaskInfo } from '../../../../models/task.model';

describe('TaskCardCauseWaitComponent', () => {
  it('names the cause card the card waits for', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardCauseWaitComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { jobs: signal([]) } },
        { provide: TaskSelectionService, useValue: { openDetail: () => undefined } },
        { provide: NotificationService, useValue: { info: () => undefined } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardCauseWaitComponent);
    fixture.componentRef.setInput('task', {
      state: TaskState.AutoReview,
      causeWait: {
        causeKey: 'AGT-2801',
        fingerprint: '3f1c0a9e5b7d2c44',
        failureClass: 'ReviewInfra/ToolUnavailable',
        since: '2026-09-09T18:06:00.000Z',
        reason: 'waiting for AGT-2801: ReviewInfra/ToolUnavailable on agent:codex:gpt-5.4-mini',
      },
    } as TaskInfo);
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-cause-wait"]') as HTMLElement;
    expect(pill).toBeTruthy();
    expect(pill.textContent).toContain('Waiting for AGT-2801');
    expect(pill.textContent).not.toContain('scalated');
    expect(pill.getAttribute('data-cause-key')).toBe('AGT-2801');
  });

  it('renders nothing without a cause wait', async () => {
    await TestBed.configureTestingModule({
      imports: [TaskCardCauseWaitComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { jobs: signal([]) } },
        { provide: TaskSelectionService, useValue: { openDetail: () => undefined } },
        { provide: NotificationService, useValue: { info: () => undefined } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TaskCardCauseWaitComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-cause-wait"]')).toBeNull();
  });
});
