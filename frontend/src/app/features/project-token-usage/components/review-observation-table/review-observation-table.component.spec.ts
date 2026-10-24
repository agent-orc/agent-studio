import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { TaskService } from '../../../../services/task.service';
import type { ReviewObservationReport } from '../../models/project-token-usage.model';
import { ReviewObservationTableComponent } from './review-observation-table.component';

describe('ReviewObservationTableComponent', () => {
  it('renders measured rates and keeps unpriced findings unknown', async () => {
    const report: ReviewObservationReport = {
      project: 'demo', generatedAt: '2026-10-21T12:00:00Z', method: 'Studio observations',
      reviews: [], laterEvidence: [{ id: 'e', reviewId: 'r', source: 'gate', evidenceRef: 'gate.md', summary: 'Miss' }],
      byModelAndReasoning: [{
        model: 'gpt-6-sol', reasoningLevel: 'high', reviews: 2, blockers: 1,
        approved: 1, missEligibleApprovals: 1, misses: 1, realFindings: null, costUsd: null,
        costPerReview: null, blockerRate: 0.5, missRate: 1, costPerRealFinding: null,
      }],
    };
    await TestBed.configureTestingModule({
      imports: [ReviewObservationTableComponent],
      providers: [provideZonelessChangeDetection(), {
        provide: TaskService,
        useValue: { getProjectReviewObservations: () => of(report) },
      }],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewObservationTableComponent);
    fixture.componentRef.setInput('projectName', 'demo');
    await fixture.whenStable();
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('gpt-6-sol');
    expect(text).toContain('50.0%');
    expect(text).toContain('100.0%');
    expect(text).toContain('Unknown');
    expect(fixture.nativeElement.querySelector('[data-testid="review-observations-export"]')?.getAttribute('href'))
      .toContain('/api/projects/demo/token-usage/review-observations/export');
  });
});
