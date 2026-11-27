import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { OffStableGradeNoticeComponent } from './off-stable-grade-notice.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '@studio/ui/services/notification.service';
import type { ReviewAttempt, TaskInfo } from '../../../../models/task.model';

const NOTICE = 'graded by release 20261105T1059Z-review-b9a3a90d6895 (commit b9a3a90), '
  + 'which differs from Stable 0.9.5 (38b28d1)';

const attempt = (overrides: Partial<ReviewAttempt>): ReviewAttempt => ({
  plane: 'remote', attemptId: 'review_bh3', receivedAt: '2026-11-05T22:15:17Z', outcome: 'Pass', grade: null,
  buildTestsResult: 'passed', buildTestsReason: null, aspects: [], subjectSha: null,
  reportRef: 'remote-review-grade-review_bh3.md', ...overrides,
});

async function mount(info: Partial<TaskInfo>, reviewAgain = vi.fn(() => of({ reviewAttemptId: 'r2', briefVersion: 'b' }))) {
  const refresh = vi.fn();
  const success = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [OffStableGradeNoticeComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { reviewAgainOnCurrentBuild: reviewAgain, refresh } },
      { provide: NotificationService, useValue: { success, error: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(OffStableGradeNoticeComponent);
  fixture.componentRef.setInput('job', {
    id: 'BH3-1', state: '5-human-review', watchPath: '/project/tasks', ...info,
  } as TaskInfo);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const click = (testId: string) => {
    host.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();
    fixture.detectChanges();
  };
  return { host, reviewAgain, refresh, success, click };
}

describe('OffStableGradeNoticeComponent (AGT-3845)', () => {
  it('names the non-Stable build that graded a Human Review card', async () => {
    const { host } = await mount({ reviewProjection: { attempts: [attempt({ workerReleaseStable: NOTICE })] } as TaskInfo['reviewProjection'] });
    expect(host.querySelector('[data-testid="off-stable-grade-notice"]')?.textContent)
      .toContain('Graded by a build that is not Stable');
    expect(host.querySelector('[data-testid="off-stable-grade-reason"]')?.textContent?.trim()).toBe(NOTICE);
    expect(host.querySelector('[data-testid="off-stable-grade-state"]')?.getAttribute('data-tone')).toBe('warning');
  });

  it('stays empty for a Stable grade, a later Stable regrade and outside Human Review', async () => {
    for (const info of [
      { reviewProjection: { attempts: [attempt({ workerReleaseStable: null })] } },
      { reviewProjection: { attempts: [attempt({ attemptId: 'r2' }), attempt({ workerReleaseStable: NOTICE })] } },
      { state: '6-completed', reviewProjection: { attempts: [attempt({ workerReleaseStable: NOTICE })] } },
      { reviewProjection: null },
    ] as Partial<TaskInfo>[]) {
      const { host } = await mount(info);
      expect(host.querySelector('[data-testid="off-stable-grade-notice"]')).toBeNull();
    }
  });

  it('requests a fresh review on the current build once', async () => {
    const { host, reviewAgain, refresh, success, click } = await mount({
      reviewProjection: { attempts: [attempt({ workerReleaseStable: NOTICE })] } as TaskInfo['reviewProjection'],
    });
    click('off-stable-grade-review-again');
    expect(reviewAgain).toHaveBeenCalledWith('BH3-1', '/project/tasks');
    expect(success).toHaveBeenCalled();
    expect(refresh).toHaveBeenCalledWith(true);
    const button = host.querySelector<HTMLButtonElement>('[data-testid="off-stable-grade-review-again"]')!;
    expect(button.disabled).toBe(true);
    expect(button.textContent?.trim()).toBe('Fresh review requested');
  });

  it('shows the server refusal', async () => {
    const { host, click } = await mount(
      { reviewProjection: { attempts: [attempt({ workerReleaseStable: NOTICE })] } as TaskInfo['reviewProjection'] },
      vi.fn(() => throwError(() => ({ error: { error: 'The current delivery has no completed run to review.' } }))),
    );
    click('off-stable-grade-review-again');
    expect(host.querySelector('[data-testid="off-stable-grade-error"]')?.textContent)
      .toContain('no completed run to review');
  });
});
