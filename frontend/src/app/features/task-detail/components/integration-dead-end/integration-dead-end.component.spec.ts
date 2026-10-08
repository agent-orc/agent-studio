import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of } from 'rxjs';
import { IntegrationDeadEndComponent } from './integration-dead-end.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';

describe('IntegrationDeadEndComponent', () => {
  it('queues a continuation on the same task when the primary action is clicked', async () => {
    const continueFromFailure = vi.fn(() => of({ status: 'queued', stage: 'integration reach', taskKey: 'AGT-2909', savedReason: 'lane-not-runnable' }));
    const refresh = vi.fn();
    const success = vi.fn();
    await TestBed.configureTestingModule({
      imports: [IntegrationDeadEndComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { continueFromFailure, refresh } },
        { provide: NotificationService, useValue: { success, error: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(IntegrationDeadEndComponent);
    fixture.componentRef.setInput('jobId', 'task-2909');
    fixture.componentRef.setInput('watchPath', '/project/tasks');
    fixture.componentRef.setInput('status', {
      status: 'pending', integrationBranch: 'develop', deliveryRef: 'task/AGT-2909',
      sha: null, detail: 'Delivery is not on develop.',
    });
    fixture.detectChanges();
    const panel = fixture.nativeElement as HTMLElement;
    expect(panel.textContent).toContain('Delivery pending');
    (panel.querySelector('.dead-end__primary') as HTMLButtonElement).click();
    expect(continueFromFailure).toHaveBeenCalledWith('task-2909', '/project/tasks');
    expect(refresh).toHaveBeenCalledWith(true);
    expect(success).toHaveBeenCalledWith('Continuation queued. Task moved to Ready.');
    expect(panel.querySelector('[data-testid="continuation-saved"]')).toBeNull();
  });

  it('shows a saved intent and offers the Ready move when promotion did not happen', async () => {
    const continueFromFailure = vi.fn(() => of({ status: 'saved', stage: 'delivery-pending', taskKey: 'AGT-2909', savedReason: 'lane-not-runnable' }));
    const moveJob = vi.fn(() => of({}));
    const success = vi.fn();
    await TestBed.configureTestingModule({
      imports: [IntegrationDeadEndComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { continueFromFailure, moveJob, refresh: vi.fn() } },
        { provide: NotificationService, useValue: { success, error: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(IntegrationDeadEndComponent);
    fixture.componentRef.setInput('jobId', 'task-2909');
    fixture.componentRef.setInput('status', { status: 'pending', detail: 'Delivery pending.' });
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.dead-end__primary')!.click();
    fixture.detectChanges();
    expect(success).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="continuation-saved"]')?.textContent)
      .toContain('lane-not-runnable');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="continuation-move-ready"]')!.click();
    expect(moveJob).toHaveBeenCalledWith('task-2909', '2-ready', undefined);
  });
});
