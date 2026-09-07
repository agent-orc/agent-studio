import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { ModelMigrationProposal } from '../../../quota';
import { ModelMigrationStore } from '../../services/model-migration.store';

/**
 * "Update available: <current> to <target>" for one pinned model, with the cost
 * and reasoning-ladder diff on hover and a one-click apply.
 *
 * One component, three hosts (task Agent row, project pipeline rows, CLI
 * Management routes). It owns its own data dependency - hand it a model id and
 * it resolves the proposal through the shared {@link ModelMigrationStore} - so a
 * host adds one tag and no logic, and the three surfaces cannot drift apart.
 *
 * It owns no policy: the proposal, including whether the migration is safe to
 * apply automatically, is computed on the backend. The host performs the write
 * through whichever mutation already owns that pin.
 */
@Component({
  selector: 'app-model-migration-hint',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './model-migration-hint.component.html',
  styleUrl: './model-migration-hint.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ModelMigrationHintComponent {
  private readonly migrations = inject(ModelMigrationStore);

  /** The pinned model to check. Empty or current renders nothing. */
  readonly model = input<string | null>(null);

  /** Disables the apply control while the host's mutation is in flight. */
  readonly busy = input(false);

  /** Hides the apply control where the host has no mutation to offer. */
  readonly applyable = input(true);
  readonly testId = input('model-migration-hint');

  /** Emits the target model id; the host owns the actual write. */
  readonly apply = output<string>();

  readonly proposal = computed<ModelMigrationProposal | null>(
    () => this.migrations.proposalFor(this.model()),
  );

  readonly summary = computed(() => {
    const offer = this.proposal();
    if (offer === null) return '';
    return `update available: ${offer.from.modelId} to ${offer.to.modelId}`;
  });

  /**
   * The diff an operator needs before accepting: what changes about cost, what
   * changes about the reasoning ladder, and why the catalog proposes it.
   */
  readonly detail = computed(() => {
    const offer = this.proposal();
    if (offer === null) return '';
    const lines = [
      `${offer.from.label} to ${offer.to.label}`,
      `Cost: ${priceLine(offer.from)} to ${priceLine(offer.to)} (${offer.costClass})`,
      `Reasoning: ${ladderLine(offer.from.thinkingLevels)} to ${ladderLine(offer.to.thinkingLevels)}`,
      `Rule: ${offer.rule} (catalog ${offer.catalogVersion})`,
    ];
    if (!offer.ladderCompatible) lines.push('The target drops a reasoning level this model offers.');
    if (offer.reason) lines.push(offer.reason);
    return lines.join('\n');
  });

  /**
   * Whether run admission would apply this on its own. Rendered as a calm state
   * word rather than a colour alone, so the distinction survives both themes.
   */
  readonly autoLabel = computed(() => (this.proposal()?.safeAuto ? 'automatic' : 'needs review'));

  constructor() {
    // Idempotent and shared: whichever surface mounts first pays for the lookup.
    this.migrations.ensure().subscribe({ error: () => void 0 });
  }

  onApply(): void {
    const offer = this.proposal();
    if (offer === null || this.busy()) return;
    this.apply.emit(offer.to.modelId);
  }
}

function priceLine(side: ModelMigrationProposal['from']): string {
  const { inputPricePerMillion: input, outputPricePerMillion: output } = side;
  return input === null || output === null ? 'unknown' : `${input}/${output} per Mtok`;
}

function ladderLine(levels: readonly string[]): string {
  return levels.length > 0 ? levels.join(' · ') : 'unknown';
}
