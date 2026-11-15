import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AspectFindingsListComponent } from '../aspect-findings';
import { StatusIndicatorComponent, type StatusTone } from '../status-indicator/status-indicator.component';
import { steeringStatusTone, type SteeringInfo } from './steering-detail.model';

/**
 * The single canonical renderer for one orchestrator steering step
 * (ASS-776). Takes a typed {@link SteeringInfo} and renders a structured,
 * collapsible block so the operator can reconstruct WHAT the orchestrator
 * decided (verdict + short reason), WHICH steer prompt the agent received, and
 * WHICH context it was given (open items, prior commits, resume info,
 * re-issue counter) — never a raw `**`/`[]` text blob.
 *
 * Reused by the Timeline tab (one block per steering event) and the Overview
 * pipeline-steps surface (under the orchestrator review / decision steps), so
 * both surfaces read steering identically.
 *
 *   <app-steering-detail [info]="info" [showVerdictChip]="true" />
 *
 * The verdict chip is the shared status indicator (AGT-3403):
 * accept → success, reissue → warning, escalate → danger, continuation → neutral.
 */
@Component({
  selector: 'app-steering-detail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AspectFindingsListComponent, StatusIndicatorComponent],
  templateUrl: './steering-detail.component.html',
  styleUrl: './steering-detail.component.scss',
})
export class SteeringDetailComponent {
  /** The structured steering step to render. */
  readonly info = input.required<SteeringInfo>();

  /**
   * Show the toned verdict chip + reason head. Hosts whose row head already
   * conveys the verdict (the Timeline row's toned kind label) pass `false` to
   * avoid duplicating it; the Steps surface passes `true`.
   */
  readonly showVerdictChip = input<boolean>(true);

  /** Shared status tone for the verdict chip. */
  readonly verdictTone = computed<StatusTone>(() => steeringStatusTone(this.info().verdict));

  /** Whether the collapsible body has anything worth expanding. */
  readonly hasBody = computed<boolean>(() => {
    const i = this.info();
    return (
      i.openItems.length > 0 ||
      i.prompt != null ||
      i.context.length > 0 ||
      i.commits.length > 0
    );
  });
}
