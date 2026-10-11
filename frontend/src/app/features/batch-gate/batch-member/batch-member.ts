import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { BatchGateService } from '../../../services/batch-gate.service';

@Component({
  selector: 'app-batch-member',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `@if (member(); as item) {
    <span class="batch-member" data-testid="batch-member-badge" [attr.data-status]="item.status">
      @switch (item.status) {
        @case ('pending') { Batch pending }
        @case ('in-batch') { Batch {{ item.batchId }} · {{ item.position ?? '?' }} of {{ item.size ?? '?' }} }
        @case ('published') { Batch published }
        @case ('fallback') { Per-task fallback }
      }
    </span>
  }`,
  styles: [`
    .batch-member { display: inline-flex; align-items: center; border: 1px solid var(--studio-border); border-radius: 999px; background: var(--studio-bg-elevated); color: var(--studio-fg-muted); padding: var(--studio-spacing-1) var(--studio-spacing-2); font-size: .72rem; font-variant-numeric: tabular-nums; }
  `],
})
export class BatchMemberComponent {
  readonly project = input.required<string>();
  readonly taskKey = input.required<string>();
  private readonly gate = inject(BatchGateService);
  readonly member = computed(() => this.gate.state(this.project())()?.members?.[this.taskKey()] ?? null);
  constructor() {
    effect(onCleanup => {
      const project = this.project();
      if (project) onCleanup(this.gate.watch(project));
    });
  }
}
