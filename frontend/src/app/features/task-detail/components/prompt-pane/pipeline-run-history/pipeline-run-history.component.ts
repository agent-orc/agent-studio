import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../../../models/task.model';
import type { RunRecord } from '../../../../run-timeline';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { RunTimelineComponent } from '../../protocol-pane/run-timeline/run-timeline.component';
import { TooltipDirective, type StructuredTooltip } from 'coding-agent-chat/shared';

@Component({
  selector: 'app-pipeline-run-history',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent, RunTimelineComponent, TooltipDirective],
  templateUrl: './pipeline-run-history.component.html',
  styleUrl: './pipeline-run-history.component.scss',
})
export class PipelineRunHistoryComponent {
  readonly job = input.required<TaskInfo>();
  readonly runs = input<RunRecord[]>([]);
  readonly runCount = input.required<number>();
  readonly countLabel = input.required<string>();
  readonly promptMarkdown = input<string | null>(null);
  readonly tooltip = input<StructuredTooltip | string | null>(null);

  readonly open = signal(false);

  show(): void {
    if (this.runs().length > 1) this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }
}
