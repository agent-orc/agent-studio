import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';

/**
 * Padded, muted text used when a panel / list / section has nothing
 * to show. Centralises the "italic, muted, padded" recipe so every
 * empty state reads the same across the app.
 *
 * Two usage shapes:
 *
 *   1. Headline + body via inputs:
 *      <app-empty-state
 *        icon="bot"
 *        title="No agents have spoken yet"
 *        body="Once the orchestrator or a CLI emits to the bus,
 *              the timeline + counters fill in here." />
 *
 *   2. Single-line variant via content projection:
 *      <app-empty-state>No projects loaded.</app-empty-state>
 *
 * Reads only studio-shell tokens so it flips per theme automatically.
 */
@Component({
  selector: 'sui-empty-state, app-empty-state',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StudioIconComponent],
  encapsulation: ViewEncapsulation.None,
  templateUrl: './empty-state.component.html',
  styleUrl: './empty-state.component.scss',
})
export class EmptyStateComponent {
  readonly icon = input<StudioIconName | null>(null);
  readonly title = input<string | null>(null);
  readonly body = input<string | null>(null);
  /** Smaller padding + body-only variant (used inside dense lists). */
  readonly compact = input(false);
  readonly testid = input<string | null>(null);
}
