import { Directive, HostListener, inject, input } from '@angular/core';
import { CostBreakdownService } from '../services/cost-breakdown.service';

interface CostRowLike {
  model?: string | null;
  modelId?: string | null;
  label?: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheWriteTokens?: number;
  cacheCreationTokens?: number;
  totalTokens?: number;
}

/** Turns any existing cost button into a launcher for the shared calculation dialog. */
@Directive({ selector: '[appCostBreakdownTrigger]', standalone: true })
export class CostBreakdownTriggerDirective {
  private readonly breakdown = inject(CostBreakdownService);
  readonly appCostBreakdownTrigger = input.required<readonly CostRowLike[]>();
  readonly costBreakdownTitle = input('Cost calculation');
  readonly costBreakdownRecordedAt = input<string | null>(null);

  @HostListener('click')
  open(): void {
    const items = this.appCostBreakdownTrigger()
      .filter(row => (row.modelId || row.model) && (row.totalTokens ?? 1) > 0)
      .map(row => ({
        model: (row.modelId || row.model)!,
        label: row.label,
        inputTokens: row.inputTokens,
        outputTokens: row.outputTokens,
        cacheReadTokens: row.cacheReadTokens,
        cacheWriteTokens: row.cacheWriteTokens ?? row.cacheCreationTokens ?? 0,
        recordedAt: this.costBreakdownRecordedAt(),
      }));
    this.breakdown.show(items, this.costBreakdownTitle());
  }
}
