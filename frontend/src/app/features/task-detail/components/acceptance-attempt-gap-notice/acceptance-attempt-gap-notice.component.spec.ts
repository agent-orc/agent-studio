import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { AcceptanceAttemptGapNoticeComponent } from './acceptance-attempt-gap-notice.component';
import type { TaskInfo } from '../../../../models/task.model';

async function mount(info: Partial<TaskInfo>) {
  await TestBed.configureTestingModule({
    imports: [AcceptanceAttemptGapNoticeComponent],
    providers: [provideZonelessChangeDetection()],
  }).compileComponents();
  const fixture = TestBed.createComponent(AcceptanceAttemptGapNoticeComponent);
  fixture.componentRef.setInput('job', { id: 'AGT-3042', state: '5-human-review', ...info } as TaskInfo);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('AcceptanceAttemptGapNoticeComponent (AGT-3076)', () => {
  it('stays empty when the run attempt lineage is intact', async () => {
    const host = await mount({ acceptanceAttemptGap: null });
    expect(host.querySelector('[data-testid="acceptance-attempt-gap"]')).toBeNull();
  });

  it('says why acceptance needs an override and names the delivery evidence', async () => {
    const host = await mount({
      acceptanceAttemptGap: {
        authorityEpoch: 2,
        subjectRunAttemptId: 'run-3042',
        resultSha: 'abcdef0123456789abcdef0123456789abcdef01',
        reason: "Acceptance refused: no run attempt in the current authority epoch (2) for 'AGT-3042'.",
      },
    });
    const notice = host.querySelector('[data-testid="acceptance-attempt-gap"]');
    expect(notice?.textContent).toContain('Acceptance needs an operator override');
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-epoch"]')?.textContent?.trim()).toBe('2');
    expect(host.querySelector('[data-testid="acceptance-attempt-gap-reason"]')?.textContent)
      .toContain('no run attempt in the current authority epoch');
    expect(notice?.textContent).toContain('run-3042');
    expect(notice?.textContent).toContain('abcdef012345');
  });
});
