import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective, type StructuredTooltip } from 'coding-agent-chat/shared';
import { buildModelLevelPresentation } from './model-level-indicator.util';
import { CliCatalogStore } from '../../features/cli';
import type { CliType } from '../../models/task.model';

@Component({
  selector: 'app-model-level-indicator',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './model-level-indicator.component.html',
  styleUrl: './model-level-indicator.component.scss',
})
export class ModelLevelIndicatorComponent {
  private readonly catalogs = inject(CliCatalogStore);
  readonly model = input<string | null>(null);
  readonly cliType = input<string | null>(null);
  readonly thinkingLevel = input<string | null>(null);
  readonly thinkingLevelOverride = input(false);
  readonly fallbackLabel = input('');
  readonly tooltip = input<string | StructuredTooltip>('');
  readonly source = input<string | null>(null);
  readonly isDefault = input(false);
  readonly testId = input('model-level-indicator');
  readonly levelTestId = input('model-level-thinking');

  readonly mappedLevel = computed(() => {
    const model = this.model();
    const level = this.thinkingLevel();
    const cli = this.cliType();
    if (!model || level !== 'ultra' || !cli) return null;
    const discovered = this.catalogs.modelsFor(cli as CliType).find((entry) => entry.id === model);
    return discovered?.thinkingLevels?.includes('xhigh') && !discovered.thinkingLevels.includes('ultra')
      ? 'xhigh' : null;
  });
  readonly effectiveLevel = computed(() => this.mappedLevel() ?? this.thinkingLevel());
  readonly effectiveTooltip = computed(() => {
    const original = this.tooltip();
    const note = this.mappedLevel() ? 'Pinned ultra is unavailable for this model; xhigh is used.' : null;
    return note && typeof original === 'string' ? `${original}\n${note}` : original;
  });

  readonly presentation = computed(() => buildModelLevelPresentation(
    this.model(),
    this.effectiveLevel(),
    this.fallbackLabel(),
  ));

  readonly accessibleLabel = computed(() => {
    const parts = [`Model ${this.model() || this.fallbackLabel() || 'unknown'}`];
    if (this.effectiveLevel()) parts.push(`thinking level ${this.effectiveLevel()}`);
    if (this.mappedLevel()) parts.push('pinned ultra unavailable, mapped to xhigh');
    if (this.cliType()) parts.push(`CLI ${this.cliType()}`);
    return parts.join(', ');
  });
}
