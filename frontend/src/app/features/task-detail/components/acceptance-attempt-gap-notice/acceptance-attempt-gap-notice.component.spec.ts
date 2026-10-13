import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { of, throwError } from 'rxjs';
import { AcceptanceAttemptGapNoticeComponent } from './acceptance-attempt-gap-notice.component';
import { TaskService } from '../../../../services/task.service';
import { NotificationService } from '../../../../services/notification.service';
import type { TaskInfo } from '../../../../models/task.model';

const GAP: NonNullable<TaskInfo['acceptanceAttemptGap']> = {
  authorityEpoch: 2,
  subjectRunAttemptId: 'run-3042',
  resultSha: 'abcdef0123456789abcdef0123456789abcdef01',
  reason: "Acceptance refused: no run attempt in the current authority epoch (2) for 'AGT-3042'.",
};

async function mount(info: Partial<TaskInfo>, moveJob = vi.fn(() => of({}))) {
  const refresh = vi.fn();
  const success = vi.fn();
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    imports: [AcceptanceAttemptGapNoticeComponent],
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: { moveJob, refresh } },
      { provide: NotificationService, useValue: { success, error: vi.fn() } },
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(AcceptanceAttemptGapNoticeComponent);
  fixture.componentRef.setInput('job', {
    id: 'AGT-3042', state: '5-human-review', watchPath: '/project/tasks', ...info,
  } as TaskInfo);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const typeReason = (text: string) => {
    const input = host.querySelector<HTMLTextAreaElement>('[data-testid="acceptance-attempt-gap-override-reason"]')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  };
  const click = (testId: string) => {
    host.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();
    fixture.detectChanges();
  };
  return { fixture, host, moveJob, refresh, success, typeReason, click };
}

describe('AcceptanceAttemptGapNoticeComponent (AGT-3076)', () => {
  it('stays empty when the run attempt lineage is intact', async () => {
    const { host } = await mount({ acceptanceAttemptGap: null });
    expect(host.querySelector('[data-testid="acceptance-attempt-gap"]')).toBeNull();
  });

  it('says why acceptance needs an override and names the delivery evidence', async () => {
    const { host } = await mount({ acceptanceAttemptGap: GAP });
    const notice = host.querySelector('[data-testid="acceptance-attempt-gap"]');
    expect(notice?.textContent).toContain('Acceptance needs an operator override');
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-epoch"]')?.textContent?.trim()).toBe('2');
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-reason"]')?.textContent)
      .toContain('no run attempt in the current authority epoch');
    expect(notice?.textContent).toContain('run-3042');
    expect(notice?.textContent).toContain('abcdef012345');
    expect(notice?.textContent).toContain('written reason');
  });
});

describe('AcceptanceAttemptGapNoticeComponent override path (AGT-3096)', () => {
  it('accepts with an operator override only after a written reason', async () => {
    const { host, moveJob, refresh, success, typeReason, click } = await mount({ acceptanceAttemptGap: GAP });

    click('acceptance-attempt-gap-override');
    const submit = host.querySelector<HTMLButtonElement>('[data-testid="acceptance-attempt-gap-submit"]')!;
    expect(submit.disabled).toBe(true);
    typeReason('   ');
    expect(submit.disabled).toBe(true);

    typeReason('  Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.  ');
    expect(submit.disabled).toBe(false);
    click('acceptance-attempt-gap-submit');

    expect(moveJob).toHaveBeenCalledWith('AGT-3042', '6-completed', '/project/tasks', undefined,
      'Integrated and deployed; the run attempt was lost in the epoch 2 rebuild.', true);
    expect(success).toHaveBeenCalled();
    expect(refresh).toHaveBeenCalledWith(true);
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-form"]')).toBeNull();
  });

  it('keeps the form open and shows the server refusal when the override is refused', async () => {
    const moveJob = vi.fn(() => throwError(() => ({
      status: 409,
      error: { error: 'Acceptance refused: the delivery is not integrated.', code: 'integration-dead-end' },
    })));
    const { host, refresh, typeReason, click } = await mount({ acceptanceAttemptGap: GAP }, moveJob);

    click('acceptance-attempt-gap-override');
    typeReason('Deployed by hand.');
    click('acceptance-attempt-gap-submit');

    expect(host.querySelector('[data-testid="acceptance-attempt-gap-error"]')?.textContent)
      .toContain('the delivery is not integrated');
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-form"]')).not.toBeNull();
    expect(refresh).not.toHaveBeenCalled();
  });

  it('cancels without moving the card', async () => {
    const { host, moveJob, typeReason, click } = await mount({ acceptanceAttemptGap: GAP });

    click('acceptance-attempt-gap-override');
    typeReason('Not sure yet.');
    click('acceptance-attempt-gap-cancel');

    expect(moveJob).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-override"]')).not.toBeNull();
  });
});
