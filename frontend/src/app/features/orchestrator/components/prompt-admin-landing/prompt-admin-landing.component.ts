import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { PromptCatalogItem, PromptProjectOverride } from '../../../../services/prompt-admin.service';
import { promptReviewStatus } from '../prompt-review-section/prompt-review-status';

interface PromptClassSummary {
  id: PromptCatalogItem['promptClass'];
  title: string;
  description: string;
  items: PromptCatalogItem[];
}

type PromptSortKey = 'title' | 'calls' | 'lastCalled' | 'lastChange' | 'lastReview' | 'cost' | 'status';

@Component({
  selector: 'app-prompt-admin-landing',
  standalone: true,
  imports: [DatePipe, DecimalPipe, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-admin-landing.component.html',
  styleUrl: './prompt-admin-landing.component.scss',
})
export class PromptAdminLandingComponent {
  readonly items = input<PromptCatalogItem[]>([]);
  readonly orphanedOverrides = input<PromptProjectOverride[]>([]);
  readonly deadPromptDays = input(30);
  readonly costDisclaimer = input('');
  readonly reviewBusy = input(false);
  readonly openPrompt = output<string>();
  readonly reviewAll = output<void>();
  readonly sortKey = signal<PromptSortKey>('calls');
  readonly sortDescending = signal(true);

  readonly classes = computed<PromptClassSummary[]>(() => {
    const definitions: Omit<PromptClassSummary, 'items'>[] = [
      {
        id: 'runtime-step',
        title: 'Runtime steps',
        description: 'Runner, review, supervisor, and utility instructions loaded by an executable code path.',
      },
      {
        id: 'orchestrator',
        title: 'Orchestrator',
        description: 'Boot, decision, recovery, and conflict-resolution instructions for orchestration flows.',
      },
      {
        id: 'drift',
        title: 'Drift and analysis',
        description: 'Prompts that compare code, architecture, documentation, tasks, and product direction.',
      },
      {
        id: 'framing',
        title: 'Mode framing',
        description: 'Small policy blocks injected into a runtime prompt for concept, read-only, or web-enabled modes.',
      },
    ];
    return definitions.map(definition => ({
      ...definition,
      items: this.items().filter(item => item.promptClass === definition.id),
    }));
  });

  readonly sortedItems = computed(() => {
    const key = this.sortKey();
    const direction = this.sortDescending() ? -1 : 1;
    return [...this.items()].sort((left, right) => {
      const compared = this.sortValue(left, key).localeCompare(
        this.sortValue(right, key),
        undefined,
        { numeric: true }
      );
      return compared * direction || left.title.localeCompare(right.title);
    });
  });

  sort(key: PromptSortKey): void {
    if (this.sortKey() === key) {
      this.sortDescending.update(value => !value);
      return;
    }
    this.sortKey.set(key);
    this.sortDescending.set(key !== 'title');
  }

  sortLabel(key: PromptSortKey): string {
    if (this.sortKey() !== key) return '';
    return this.sortDescending() ? ' ▼' : ' ▲';
  }

  readonly reviewStatus = promptReviewStatus;

  /** A prompt called within deadPromptDays is active; a quiet one stays neutral. */
  activityTone(item: PromptCatalogItem): StatusTone {
    return item.calls.isDead ? 'neutral' : 'success';
  }

  cost(item: PromptCatalogItem, sevenDays = false): string {
    const calls = sevenDays ? item.calls.calls7d : item.calls.totalCalls;
    const value = sevenDays ? item.calls.costUsd7d : item.calls.costUsd;
    const unpriced = sevenDays ? item.calls.unpricedCalls7d : item.calls.unpricedCalls;
    if (calls > 0 && unpriced === calls) return 'Unknown';
    const digits = value >= 1 ? 2 : value >= 0.01 ? 4 : 6;
    return `$${value.toFixed(digits)}${unpriced ? '*' : ''}`;
  }

  sparkHeight(item: PromptCatalogItem, calls: number): string {
    const max = Math.max(1, ...item.calls.daily.map(day => day.calls));
    return calls === 0 ? '2px' : `${Math.max(12, Math.round((calls / max) * 100))}%`;
  }

  private sortValue(item: PromptCatalogItem, key: PromptSortKey): string {
    switch (key) {
      case 'calls': return String(item.calls.totalCalls).padStart(12, '0');
      case 'lastCalled': return item.calls.lastCalledAt ?? '';
      case 'lastChange': return item.lastChangedAt ?? '';
      case 'lastReview': return item.lastReviewedAt ?? '';
      case 'cost': return String(item.calls.costUsd).padStart(20, '0');
      case 'status': return item.reviewStatus ?? '';
      default: return item.title;
    }
  }
}
