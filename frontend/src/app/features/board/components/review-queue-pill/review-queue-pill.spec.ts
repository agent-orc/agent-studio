import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import { ReviewQueuePillComponent, reviewPriorityTone } from './review-queue-pill';

describe('ReviewQueuePillComponent', () => {
  it('maps review priority to a status tone and falls back to neutral', () => {
    expect(['urgent', 'normal', undefined, 'later'].map(reviewPriorityTone))
      .toEqual(['warning', 'neutral', 'neutral', 'neutral']);
  });

  it('shows an urgent queue position as a warning indicator', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewQueuePillComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewQueuePillComponent);
    fixture.componentRef.setInput('task', {
      state: TaskState.AutoReview, reviewQueuePosition: 1, reviewPriority: 'urgent',
    } as TaskInfo);
    fixture.detectChanges();

    const pill = fixture.nativeElement.querySelector('[data-testid="task-card-review-queue"]') as HTMLElement;
    expect(pill.textContent?.trim()).toBe('Urgent · Review #1');
    expect(pill.dataset['tone']).toBe('warning');
  });
});
