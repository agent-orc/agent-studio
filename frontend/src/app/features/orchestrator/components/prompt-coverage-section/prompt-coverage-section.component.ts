import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { PromptCoverageResponse } from '../../../../services/prompt-admin.service';

const COVERAGE_TONES: Record<string, StatusTone> = {
  covered: 'success',
  pending: 'warning',
};

/** Tone of one component's inline-migration coverage; unknown states are neutral. */
export function promptCoverageTone(status: string): StatusTone {
  return COVERAGE_TONES[status] ?? 'neutral';
}

@Component({
  selector: 'app-prompt-coverage-section',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './prompt-coverage-section.component.html',
  styleUrl: './prompt-coverage-section.component.scss',
})
export class PromptCoverageSectionComponent {
  readonly coverage = input.required<PromptCoverageResponse>();
  readonly coverageTone = promptCoverageTone;
}
