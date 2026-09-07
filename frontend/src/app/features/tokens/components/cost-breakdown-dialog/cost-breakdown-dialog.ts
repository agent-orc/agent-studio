import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { CostBreakdownService, type CostBreakdownResultItem } from '../../services/cost-breakdown.service';
import { formatTokenCurrencyUsd, formatTokenExactCount } from '../../token-number-format.util';

@Component({
  selector: 'app-cost-breakdown-dialog',
  standalone: true,
  imports: [DialogComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cost-breakdown-dialog.html',
  styleUrl: './cost-breakdown-dialog.scss',
})
export class CostBreakdownDialogComponent {
  readonly breakdown = inject(CostBreakdownService);
  readonly grandTotal = computed(() => this.breakdown.items()
    .filter(item => item.estimate.modelKnown)
    .reduce((sum, item) => sum + item.estimate.total, 0));
  readonly allPriced = computed(() => this.breakdown.items().every(item => item.estimate.modelKnown));

  formatNumber(value: number): string {
    return formatTokenExactCount(value);
  }

  formatRate(value: number): string {
    return `$${value.toFixed(value < 1 ? 4 : 2)}`;
  }

  formatUsd(value: number): string {
    return formatTokenCurrencyUsd(value);
  }

  formatDate(value: string): string {
    const date = new Date(value);
    if (date.getUTCFullYear() <= 1) return 'Base rate (start date not published)';
    return new Intl.DateTimeFormat('en-CA', { year: 'numeric', month: '2-digit', day: '2-digit' }).format(date);
  }

  formula(item: CostBreakdownResultItem): string {
    const price = item.estimate.priceBasis;
    if (!price) return 'No price was available for this model and calculation date.';
    const billableInput = item.billableInputTokens ?? item.inputTokens;
    return `(${this.formatNumber(billableInput)} / 1M × ${this.formatRate(price.inputPerMillion)}) + `
      + `(${this.formatNumber(item.outputTokens)} / 1M × ${this.formatRate(price.outputPerMillion)}) + `
      + `(${this.formatNumber(item.cacheReadTokens)} / 1M × ${this.formatRate(price.cacheReadPerMillion)}) + `
      + `(${this.formatNumber(item.cacheWriteTokens)} / 1M × ${this.formatRate(price.cacheWritePerMillion)}) = `
      + `${this.formatUsd(item.estimate.inputUsd)} + ${this.formatUsd(item.estimate.outputUsd)} + `
      + `${this.formatUsd(item.estimate.cacheReadUsd)} + ${this.formatUsd(item.estimate.cacheWriteUsd)} = `
      + this.formatUsd(item.estimate.total);
  }

  hasAdjustedInput(item: CostBreakdownResultItem): boolean {
    return item.billableInputTokens !== undefined
      && item.billableInputTokens !== item.inputTokens;
  }
}
