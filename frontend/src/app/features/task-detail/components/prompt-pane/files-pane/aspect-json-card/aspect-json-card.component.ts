import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MarkdownViewComponent } from 'coding-agent-chat/markdown';
import {
  aspectToneStatusTone,
  aspectVerdictLabel,
  aspectVerdictTone,
  type AspectVerdictTone,
} from '../../../../../../components/aspect-findings/aspect-findings.model';
import { StatusIndicatorComponent } from '../../../../../../components/status-indicator/status-indicator.component';
import type { AspectDocument } from '../aspect-document.model';

/**
 * Structured renderer for a `aspect-{id}.json` artefact (concept doc §5).
 * Replaces the raw markdown dump with a meta header (aspect title +
 * tone-coloured status badge + model), the one-line summary, an optional
 * metrics strip, and the model's narrative details. The verdict tone reuses
 * the central {@link aspectVerdictTone} mapping so the badge matches every
 * other aspect surface (pass → ok, concerns → warn, block → danger).
 *
 * `compact` mode (the collapsed Files-tab preview) shows only the badge +
 * summary; the expanded card adds the meta row and the details body.
 */
@Component({
  selector: 'app-aspect-json-card',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownViewComponent, StatusIndicatorComponent],
  templateUrl: './aspect-json-card.component.html',
  styleUrl: './aspect-json-card.component.scss',
})
export class AspectJsonCardComponent {
  readonly doc = input.required<AspectDocument>();
  /** Collapsed preview: badge + summary only, no meta row or details. */
  readonly compact = input(false);

  readonly tone = computed<AspectVerdictTone>(() => aspectVerdictTone(this.doc().status));
  readonly statusTone = computed(() => aspectToneStatusTone(this.tone()));
  readonly statusLabel = computed(() => aspectVerdictLabel(this.doc().status));

  /** Human title for the aspect id, e.g. `code-quality` → `Code quality`. */
  readonly title = computed(() => {
    const aspect = this.doc().aspect;
    if (!aspect) return 'Aspect';
    const spaced = aspect.replace(/[-_]+/g, ' ').trim();
    return spaced.charAt(0).toUpperCase() + spaced.slice(1);
  });

  readonly metricEntries = computed(() => {
    const metrics = this.doc().metrics;
    if (!metrics) return [];
    return Object.entries(metrics).map(([key, value]) => ({ key, value }));
  });
}
