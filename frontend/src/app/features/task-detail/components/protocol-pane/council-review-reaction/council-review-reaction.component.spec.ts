import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';

import type { TaskInfo } from '../../../../../models/task.model';
import { CouncilReviewReactionComponent } from './council-review-reaction.component';

describe('CouncilReviewReactionComponent', () => {
  const job = { id: 'demo-job', key: 'AGT-2108' } as TaskInfo;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CouncilReviewReactionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  it('renders an explicit missing-reaction audit state for legacy or manual reviews', () => {
    const fixture = TestBed.createComponent(CouncilReviewReactionComponent);
    fixture.componentRef.setInput('job', job);
    fixture.componentRef.setInput('reaction', null);
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain(
      'No orchestrator reaction recorded for this review.',
    );
    expect(root.querySelector<HTMLElement>('section')?.dataset['disposition']).toBe('missing');
  });

  it('renders each finding assessment and links the started round', () => {
    const fixture = TestBed.createComponent(CouncilReviewReactionComponent);
    fixture.componentRef.setInput('job', job);
    fixture.componentRef.setInput('reaction', {
      createdAt: '2026-07-23T10:00:00Z',
      reviewFileName: 'code-review-grade.md',
      grade: 'B',
      disposition: 'Reissue',
      summary: 'Fix 1 review finding in the next round.',
      startsNewRound: true,
      targetJobId: 'demo-job',
      targetRunAttempt: 2,
      assessments: [{
        finding: 'Dark-theme colors are incorrect; provide both-theme screenshots.',
        action: 'FixNextRound',
        reason: 'Concrete review deficiency.',
      }],
    });
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent).toContain('Dark-theme colors are incorrect');
    expect(root.textContent).toContain('Fix next round');
    expect(root.querySelector('[data-testid="code-review-council-action"]')?.getAttribute('data-tone')).toBe('warning');
    expect(root.querySelector<HTMLElement>('section')?.dataset['disposition']).toBe('reissue');
    expect(root.querySelector('a')?.getAttribute('href')).toContain('#/tasks/AGT-2108');
  });
});
