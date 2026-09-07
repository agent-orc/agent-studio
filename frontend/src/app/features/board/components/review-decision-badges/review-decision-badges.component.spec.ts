import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { ReviewDecisionBadgesComponent } from './review-decision-badges.component';

describe('ReviewDecisionBadgesComponent', () => {
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
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]')).toBeNull();
  });

  it('renders the AGT-2749 failure class with its retry counter on a parked card', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewDecisionBadgesComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewDecisionBadgesComponent);
    fixture.componentRef.setInput('job', {
      state: '5-human-review',
      integration: {
        status: 'pending',
        failure: {
          failureClass: 'infrastructure',
          failureSignature: 'gate-budget-exceeded',
          retryAttempt: 2,
          retryBudget: 3,
        },
      },
    } as TaskInfo);
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('[data-testid="task-card-failure-class"]');
    expect(badge?.textContent).toContain('Infrastructure');
    expect(badge?.textContent).toContain('retry 2/3');
    expect(badge?.getAttribute('data-failure-class')).toBe('infrastructure');
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
