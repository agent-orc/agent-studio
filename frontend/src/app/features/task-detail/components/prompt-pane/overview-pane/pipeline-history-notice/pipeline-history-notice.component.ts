import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../../../components/status-indicator/status-indicator.component';

@Component({
  selector: 'app-pipeline-history-notice',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-history-notice.component.html',
  styleUrl: './pipeline-history-notice.component.scss',
})
export class PipelineHistoryNoticeComponent {
  readonly attempt = input.required<number>();
  readonly currentAttempt = input.required<number>();
}
