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

  it('hides a resolved parked blocker on an Accepted card', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewDecisionBadgesComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewDecisionBadgesComponent);
    fixture.componentRef.setInput('job', {
      state: '6-completed', parkedBlocker: { blockerType: 'decision' },
    } as TaskInfo);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-parked"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="task-card-human-review"]')).toBeNull();
  });
});
