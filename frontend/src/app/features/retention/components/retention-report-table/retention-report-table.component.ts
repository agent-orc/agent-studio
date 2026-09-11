import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import { formatRetentionBytes, type RetentionAction, type RetentionPlan } from '../../models/retention.model';

type SortKey = 'taskKey' | 'project' | 'lane' | 'terminalAt' | 'bytes' | 'ruleId';

@Component({
  selector: 'app-retention-report-table',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './retention-report-table.component.html',
  styleUrl: './retention-report-table.component.scss',
})
export class RetentionReportTableComponent {
  readonly plan = input.required<RetentionPlan>();
  readonly sortKey = signal<SortKey>('taskKey');
  readonly ascending = signal(true);

  readonly actions = computed(() => {
    const key = this.sortKey();
    const direction = this.ascending() ? 1 : -1;
    return [...this.plan().actions].sort((left, right) => {
      const a = this.sortValue(left, key);
      const b = this.sortValue(right, key);
      return (typeof a === 'number' && typeof b === 'number'
        ? a - b
        : String(a).localeCompare(String(b))) * direction;
    });
  });

  sort(key: SortKey): void {
    if (this.sortKey() === key) this.ascending.update(value => !value);
    else {
      this.sortKey.set(key);
      this.ascending.set(true);
    }
  }

  ariaSort(key: SortKey): 'ascending' | 'descending' | 'none' {
    return this.sortKey() === key ? (this.ascending() ? 'ascending' : 'descending') : 'none';
  }

  bytes(value: number): string { return formatRetentionBytes(value); }

  date(value: string | null | undefined): string {
    if (!value) return '–';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '–' : date.toLocaleDateString();
  }

  private sortValue(action: RetentionAction, key: SortKey): string | number {
    if (key === 'bytes') return action.bytes;
    if (key === 'terminalAt') return action.terminalAt ? new Date(action.terminalAt).getTime() : Number.MAX_SAFE_INTEGER;
    return action[key] ?? '';
  }
}
