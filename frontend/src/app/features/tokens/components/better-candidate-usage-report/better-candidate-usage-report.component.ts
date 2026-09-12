import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { BetterCandidateUsageLine } from '../../models/tokens.model';
import { formatCompactTokens, formatCompactUsd } from '../../token-number-format.util';

@Component({
  selector: 'app-better-candidate-usage-report',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './better-candidate-usage-report.component.html',
  styleUrl: './better-candidate-usage-report.component.scss',
})
export class BetterCandidateUsageReportComponent {
  readonly lines = input<readonly BetterCandidateUsageLine[]>([]);
  formatTokens = formatCompactTokens;
  formatUsd = formatCompactUsd;

  formatWeek(start: string, end: string): string {
    const startDate = new Date(`${start}T00:00:00Z`);
    const endDate = new Date(`${end}T00:00:00Z`);
    endDate.setUTCDate(endDate.getUTCDate() - 1);
    const format = new Intl.DateTimeFormat(undefined, {
      month: 'short', day: 'numeric', timeZone: 'UTC',
    });
    return `${format.format(startDate)} to ${format.format(endDate)}`;
  }
}
