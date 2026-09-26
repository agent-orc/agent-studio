import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { NotificationService } from '../../../../services/notification.service';
import { TaskService } from '../../../../services/task.service';
import { CrashRecoveryPromptComponent } from './crash-recovery-prompt.component';

describe('CrashRecoveryPromptComponent', () => {
  it('compiles and loads pending crash recovery items', async () => {
    await TestBed.configureTestingModule({
      imports: [CrashRecoveryPromptComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getPendingCrashRecoveries: () => of({ pending: [] }),
            commitCrashRecovery: () => of({ status: 'committed', pending: null, commitSha: 'abc123', error: null }),
            dismissCrashRecovery: () => of({ status: 'dismissed', pending: null, commitSha: null, error: null }),
          },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CrashRecoveryPromptComponent);
    fixture.detectChanges();

    expect(fixture.componentInstance).toBeTruthy();
    expect(fixture.componentInstance.pending()).toEqual([]);
  });

  it('leave-all dismisses every pending item in sequence and closes the dialog', async () => {
    const items = [
      { id: 'a', projectName: 'P1', jobId: null, reason: 'r', repoRoot: 'x', message: 'm', files: [], createdAt: '2026-07-18T00:00:00Z' },
      { id: 'b', projectName: 'P2', jobId: null, reason: 'r', repoRoot: 'y', message: 'm', files: [], createdAt: '2026-07-18T00:00:00Z' },
    ];
    const dismissed = vi.fn(() => of({ status: 'dismissed', pending: null, commitSha: null, error: null }));
    await TestBed.configureTestingModule({
      imports: [CrashRecoveryPromptComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getPendingCrashRecoveries: () => of({ pending: items }),
            commitCrashRecovery: () => of({ status: 'committed', pending: null, commitSha: 'abc123', error: null }),
            dismissCrashRecovery: dismissed,
          },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CrashRecoveryPromptComponent);
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
    fixture.componentInstance.openRecovery();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    // app-dialog renders into an overlay on document.body, not under the fixture.
    const button = document.querySelector<HTMLButtonElement>('[data-testid="crash-recovery-dismiss-all"]');
    expect(button).toBeTruthy();
    button!.click();
    fixture.detectChanges();

    expect(dismissed).toHaveBeenCalledTimes(2);
    expect(dismissed).toHaveBeenNthCalledWith(1, 'a');
    expect(dismissed).toHaveBeenNthCalledWith(2, 'b');
    expect(fixture.componentInstance.pending()).toEqual([]);
    expect(fixture.componentInstance.busyAll()).toBe(false);
    expect(fixture.componentInstance.open()).toBe(false);
  });

  it('keeps trivial sidecars out of the review dialog and bulk review action', async () => {
    const review = { id: 'review', projectName: 'Review', jobId: 'AGT-1', reason: 'r', repoRoot: 'x', message: 'm', files: ['source.ts'], createdAt: '2026-07-18T00:00:00Z', classification: 'review-required' as const };
    const trivial = { id: 'trivial', projectName: 'Sidecar', jobId: null, reason: 'r', repoRoot: 'y', message: 'm', files: ['docs/page.md.meta.json'], createdAt: '2026-07-18T00:00:00Z', classification: 'trivial' as const };
    const dismissed = vi.fn(() => of({ status: 'dismissed', pending: null, commitSha: null, error: null }));
    await TestBed.configureTestingModule({
      imports: [CrashRecoveryPromptComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: TaskService, useValue: {
          getPendingCrashRecoveries: () => of({ pending: [review, trivial] }),
          commitCrashRecovery: () => of({ status: 'committed', pending: null, commitSha: 'abc123', error: null }),
          dismissCrashRecovery: dismissed,
        } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CrashRecoveryPromptComponent);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    expect(component.reviewPending()).toEqual([review]);
    expect(component.trivialPending()).toEqual([trivial]);
    component.openRecovery();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="crash-recovery-item-review"]')).toBeTruthy();
    expect(document.querySelector('[data-testid="crash-recovery-item-trivial"]')).toBeNull();
    component.dismissAll();
    fixture.detectChanges();
    expect(dismissed).toHaveBeenCalledExactlyOnceWith('review');
    expect(component.pending()).toEqual([trivial]);
    expect(component.open()).toBe(false);
    expect(TestBed.inject(NotificationService).notifications().some(item => item.title === 'Crash recovery found read-evidence sidecars')).toBe(true);
  });

  it('routes unattributed metadata sidecars to a non-blocking leave-uncommitted notification', async () => {
    const items = [
      {
        id: 'sidecar',
        projectName: 'Coding Agent Runner',
        jobId: null,
        reason: 'r',
        repoRoot: 'x',
        message: 'm',
        files: ['docs/runner.md.meta.json'],
        createdAt: '2026-07-30T00:00:00Z',
        classification: 'trivial' as const,
      },
    ];
    const dismissed = vi.fn(() => of({ status: 'dismissed', pending: null, commitSha: null, error: null }));
    await TestBed.configureTestingModule({
      imports: [CrashRecoveryPromptComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        {
          provide: TaskService,
          useValue: {
            getPendingCrashRecoveries: () => of({ pending: items }),
            commitCrashRecovery: () => of({ status: 'committed', pending: null, commitSha: 'abc123', error: null }),
            dismissCrashRecovery: dismissed,
          },
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(CrashRecoveryPromptComponent);
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
    expect(fixture.componentInstance.trivialPending()).toEqual(items);
    expect(document.querySelector('[data-testid="crash-recovery-prompt"]')).toBeNull();
    const notification = TestBed.inject(NotificationService).notifications()[0];
    expect(notification.title).toBe('Crash recovery found read-evidence sidecars');
    expect(notification.actions?.map(action => action.label)).toEqual(['Leave uncommitted']);

    notification.actions?.[0]?.callback();
    expect(dismissed).toHaveBeenCalledWith('sidecar');
    expect(fixture.componentInstance.pending()).toEqual([]);
  });
});
