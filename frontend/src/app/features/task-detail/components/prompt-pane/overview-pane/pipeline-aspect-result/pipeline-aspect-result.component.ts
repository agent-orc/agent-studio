import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { PipelineRowVm } from '../pipeline-row.vm';

@Component({
  selector: 'app-pipeline-aspect-result',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-aspect-result.component.html',
  styleUrl: './pipeline-aspect-result.component.scss',
})
export class PipelineAspectResultComponent {
  readonly row = input.required<PipelineRowVm>();
  readonly reportFile = input<string | null>(null);
  readonly documentRequested = output<string>();
}
