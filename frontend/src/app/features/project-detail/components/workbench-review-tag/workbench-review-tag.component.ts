import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { WorkbenchReview } from '../../../../models/project-docs.model';
import { NowTickService } from '../../../../services/now-tick.service';

@Component({
  selector: 'app-workbench-review-tag',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-review-tag.component.html',
  styleUrl: './workbench-review-tag.component.scss',
})
export class WorkbenchReviewTagComponent {
  readonly review = input<WorkbenchReview | null | undefined>(null);
  readonly reviewDue = input(false);
  readonly projectName = input<string | null>(null);
  private readonly now = inject(NowTickService).now;

  readonly label = computed(() => {
    const review = this.review();
    if (!review) return 'Never reviewed';
    return `${review.verdict.replaceAll('-', ' ')} · reviewed ${reviewAge(review.reviewedAt, this.now())}`;
  });
  readonly tooltip = computed(() => {
    const review = this.review();
    if (!review) return 'Verdict: never reviewed';
    return [
      `Verdict: ${review.verdict.replaceAll('-', ' ')}`,
      `Review due: ${this.reviewDue() ? 'Yes' : 'No'}`,
      `Reviewed at: ${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(review.reviewedAt))}`,
      `Review age: ${reviewAge(review.reviewedAt, this.now())}`,
      `Reviewed by: ${review.reviewedBy}`,
      `Superseded by: ${review.supersededBy.length ? '' : 'None'}`,
      `Note: ${review.note}`,
    ].join('\n');
  });
  readonly links = computed(() => (this.review()?.supersededBy ?? []).map(key => ({
    label: key,
    href: this.projectName()
      ? `#/projects/${encodeURIComponent(this.projectName()!)}/workbenches?dossier=${encodeURIComponent(`q=${key}`)}`
      : `#/workbenches?dossier=${encodeURIComponent(`q=${key}`)}`,
  })));
}

function reviewAge(value: string, now: number): string {
  const days = Math.max(0, Math.floor((now - Date.parse(value)) / 86_400_000));
  if (days === 0) return 'today';
  return new Intl.RelativeTimeFormat(undefined, { numeric: 'always' }).format(-days, 'day');
}
