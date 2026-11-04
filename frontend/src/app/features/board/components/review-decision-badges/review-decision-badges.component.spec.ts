import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { ReviewDecisionBadgesComponent, parkKindTone } from './review-decision-badges.component';

describe('ReviewDecisionBadgesComponent', () => {
  it('maps the park kinds to status tones and falls back to neutral', () => {
    expect(['decision', 'parked', 'later-kind'].map(parkKindTone)).toEqual(['info', 'warning', 'neutral']);
  });

  it('renders current Review impact without reviving a stale verdict', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewDecisionBadgesComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewDecisionBadgesComponent);
    fixture.componentRef.setInput('job', {
      state: '5-human-review',
      orchestratorVerdict: 'escalate',
      transitiveWaiters: { count: 2, keys: ['AGT-1', 'AGT-2'] },
    } as TaskInfo);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-decision-dam"]')?.textContent)
      .toContain('Dams 2 cards');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-decision-dam"]')?.getAttribute('data-tone'))
      .toBe('danger');
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]')).toBeNull();
  });

  it('renders Escalated only for the current Escalated lane', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewDecisionBadgesComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewDecisionBadgesComponent);
    fixture.componentRef.setInput('job', {
      state: '5e-escalated',
      orchestratorVerdict: null,
    } as TaskInfo);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]')?.textContent)
      .toContain('Escalated');
  });
});
