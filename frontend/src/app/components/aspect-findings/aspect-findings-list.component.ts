import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../status-indicator/status-indicator.component';
import {
  aspectVerdictLabel,
  aspectVerdictStatusTone,
  type AspectFinding,
} from './aspect-findings.model';

/**
 * The single canonical renderer for aspect-runner findings. Takes a typed
 * {@link AspectFinding}[] (resolved upstream from the structured
 * `details["findings"]` JSON or by parsing the legacy `**{aspect}**
 * [{verdict}]: {reason}` blob) and renders it as a list of rows, each with
 * the aspect name, a tone-coloured verdict chip, and the reason — so no
 * surface prints raw `**`/`[]` markdown.
 *
 * Reused across the Timeline reopen rows, the Overview completion-loop
 * strip, and (where the orchestrator chat shows aspect verdicts) the chat
 * log. Chip tones come exclusively from the central severity tokens
 * (ASS-737): concerns → warning, block → danger, pass → success,
 * rendered through the shared status indicator (AGT-3403).
 *
 *   <app-aspect-findings-list [findings]="findings" leadLabel="Gap" />
 */
@Component({
  selector: 'app-aspect-findings-list',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './aspect-findings-list.component.html',
  styleUrl: './aspect-findings-list.component.scss',
})
export class AspectFindingsListComponent {
  /** The findings to render. An empty array renders nothing. */
  readonly findings = input.required<AspectFinding[]>();
  /** Optional short lead label shown before the list (e.g. "Gap"). */
  readonly leadLabel = input<string | null>(null);

  tone(verdict: string): StatusTone {
    return aspectVerdictStatusTone(verdict);
  }

  chipLabel(verdict: string): string {
    return aspectVerdictLabel(verdict);
  }
}
