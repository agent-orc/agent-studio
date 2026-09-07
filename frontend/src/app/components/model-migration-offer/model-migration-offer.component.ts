import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TooltipDirective, type StructuredTooltip } from 'coding-agent-chat/shared';
import { PendingButtonDirective } from '../async-feedback';
import type { ModelMigrationProposal } from '../../models/model-migration.model';

@Component({
  selector: 'app-model-migration-offer',
  standalone: true,
  imports: [PendingButtonDirective, TooltipDirective],
  templateUrl: './model-migration-offer.component.html',
  styleUrl: './model-migration-offer.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModelMigrationOfferComponent {
  readonly proposal = input.required<ModelMigrationProposal>();
  readonly compact = input(false);
  readonly busy = input(false);
  readonly disabled = input(false);
  readonly actionLabel = input('Apply update');
  readonly testId = input('model-migration-offer');
  readonly applyRequested = output<ModelMigrationProposal>();

  readonly fromReasoning = computed(() => this.reasoningLabel(this.proposal().fromReasoningLevels));
  readonly toReasoning = computed(() => this.reasoningLabel(this.proposal().toReasoningLevels));
  readonly applyDisabled = computed(() => this.disabled() || this.proposal().targetAvailable !== true);
  readonly impactTooltip = computed<StructuredTooltip>(() => ({
    title: `${this.proposal().from} to ${this.proposal().to}`,
    body: [
      `Cost: ${this.proposal().costClassFrom} to ${this.proposal().costClassTo}`,
      `Reasoning: ${this.fromReasoning()} to ${this.toReasoning()}`,
      this.proposal().ladderCompatible ? 'Reasoning ladder compatible' : 'Reasoning ladder requires review',
      this.proposal().note,
    ].filter(Boolean).join('\n'),
  }));

  requestApply(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (this.busy() || this.applyDisabled()) return;
    this.applyRequested.emit(this.proposal());
  }

  private reasoningLabel(levels: readonly string[]): string {
    return levels.length > 0 ? levels.join(', ') : 'not reported';
  }
}
