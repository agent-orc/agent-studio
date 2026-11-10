import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { IntegrationDeadEndComponent } from './integration-dead-end.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';

const AVAILABLE = () => of({ available: true, reason: null });

describe('IntegrationDeadEndComponent', () => {
  it('queues a continuation on the same task when the primary action is clicked', async () => {
    const continueFromFailure = vi.fn(() => of({ status: 'queued', stage: 'integration reach', taskKey: 'AGT-2909', savedReason: 'lane-not-runnable' }));
    const refresh = vi.fn();
    const success = vi.fn();
    await TestBed.configureTestingModule({
      imports: [IntegrationDeadEndComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: TaskService, useValue: { continueFromFailure, refresh, checkFailureContinuation: AVAILABLE } },
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
    await fixture.whenStable();
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
        { provide: TaskService, useValue: { continueFromFailure, moveJob, refresh: vi.fn(), checkFailureContinuation: AVAILABLE } },
        { provide: NotificationService, useValue: { success, error: vi.fn() } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(IntegrationDeadEndComponent);
    fixture.componentRef.setInput('jobId', 'task-2909');
    fixture.componentRef.setInput('status', { status: 'pending', detail: 'Delivery pending.' });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.dead-end__primary')!.click();
    fixture.detectChanges();
    expect(success).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="continuation-saved"]')?.textContent)
      .toContain('lane-not-runnable');
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="continuation-move-ready"]')!.click();
    expect(moveJob).toHaveBeenCalledWith('task-2909', '2-ready', undefined);
  });

  describe('offers the continuation only when the server would continue it (AGT-3582)', () => {
    async function mount(service: Record<string, unknown>) {
      const error = vi.fn();
      TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [IntegrationDeadEndComponent],
        providers: [
          provideZonelessChangeDetection(),
          { provide: TaskService, useValue: { refresh: vi.fn(), ...service } },
          { provide: NotificationService, useValue: { success: vi.fn(), error } },
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(IntegrationDeadEndComponent);
      fixture.componentRef.setInput('jobId', 'AGT-3582');
      fixture.componentRef.setInput('watchPath', '/project/tasks');
      fixture.componentRef.setInput('status', {
        status: 'not-applicable', integrationBranch: 'develop', deliveryRef: null, sha: null, detail: null,
      });
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      return { host: fixture.nativeElement as HTMLElement, error };
    }

    it('hides the action and names the refusal for a delivery without a failure', async () => {
      const checkFailureContinuation = vi.fn(() => of({
        available: false, reason: 'The current delivery has no failure to continue.',
      }));
      const { host } = await mount({ checkFailureContinuation, continueFromFailure: vi.fn() });

      expect(checkFailureContinuation).toHaveBeenCalledWith('AGT-3582', '/project/tasks');
      expect(host.querySelector('[data-testid="dead-end-continue"]')).toBeNull();
      expect(host.querySelector('[data-testid="dead-end-continue-unavailable"]')?.textContent)
        .toContain('no failure to continue');
    });

    it('disables the action with the batch isolation reason as a tooltip', async () => {
      const continueFromFailure = vi.fn();
      const reason = 'AGT-3582 is a member of batch flight flight-1, which is isolating a red result.';
      const { host } = await mount({
        checkFailureContinuation: vi.fn(() => of({ available: false, reason, isolationActive: true })),
        continueFromFailure,
      });
      const action = host.querySelector<HTMLButtonElement>('[data-testid="dead-end-continue"]');
      expect(action?.disabled).toBe(true);
      expect(action?.title).toBe(reason);
      action?.click();
      expect(continueFromFailure).not.toHaveBeenCalled();
    });

    it('keeps the action hidden when the check itself fails', async () => {
      const { host } = await mount({
        checkFailureContinuation: vi.fn(() => throwError(() => ({ status: 500, error: {} }))),
        continueFromFailure: vi.fn(),
      });
      expect(host.querySelector('[data-testid="dead-end-continue"]')).toBeNull();
    });

    it('shows the server refusal in the toast', async () => {
      const continueFromFailure = vi.fn(() => throwError(() => ({
        status: 409, error: { error: 'The recorded failure belongs to an earlier delivery. Re-check the task.' },
      })));
      const { host, error } = await mount({ checkFailureContinuation: AVAILABLE, continueFromFailure });

      host.querySelector<HTMLButtonElement>('[data-testid="dead-end-continue"]')!.click();

      expect(error).toHaveBeenCalledWith('The recorded failure belongs to an earlier delivery. Re-check the task.');
    });
  });
});
