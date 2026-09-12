import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { WorkbenchReviewTagComponent } from './workbench-review-tag.component';

describe('WorkbenchReviewTagComponent', () => {
  it('shows a neutral never-reviewed tag and an immediate detailed tooltip with links', async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [WorkbenchReviewTagComponent] }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchReviewTagComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('review', {
      verdict: 'partially-superseded',
      supersededBy: ['AGT-W51'],
      reviewedAt: new Date(Date.now() - 3 * 86_400_000).toISOString(),
      reviewedBy: 'AGT-2784',
      note: 'The data model still applies.',
    });
    fixture.detectChanges();

    const tag = fixture.nativeElement.querySelector('[data-testid="workbench-review-tag"]') as HTMLElement;
    tag.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    vi.advanceTimersByTime(0);
    const tooltip = document.querySelector('[data-testid="workbench-review-tooltip"]') as HTMLElement;
    expect(tag.textContent).toContain('reviewed 3 days ago');
    expect(tooltip.textContent).toContain('Verdict: partially superseded');
    expect(tooltip.textContent).toContain('Reviewed at:');
    expect(tooltip.textContent).toContain('Reviewed by: AGT-2784');
    expect(tooltip.textContent).toContain('Superseded by:');
    expect(tooltip.textContent).toContain('The data model still applies.');
    expect(tooltip.querySelector('a')?.textContent).toBe('AGT-W51');
    expect(tooltip.querySelector('a')?.getAttribute('href')).toContain('/workbenches?dossier=');

    fixture.componentRef.setInput('reviewDue', true);
    fixture.detectChanges();
    expect(tag.textContent).toContain('Review due · partially superseded · reviewed 3 days ago');

    fixture.componentRef.setInput('review', null);
    fixture.detectChanges();
    expect(tag.textContent).toContain('Never reviewed');
    fixture.destroy();
    vi.useRealTimers();
  });
});
