import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { TaskTimelinePollService } from '../../../../../polling/services/task-timeline-poll.service';
import { TIMELINE_KIND } from '../../../../../task-timeline';
import { StatusIndicatorComponent } from '../../../../../../components/status-indicator/status-indicator.component';
import { steeringReceiptTone } from './steering-receipt-tone';

/** Current authority receipt in the task status block. History lives in Timeline. */
@Component({
  selector: 'app-steering-receipt-status',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './steering-receipt-status.component.html',
  styleUrl: './steering-receipt-status.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SteeringReceiptStatusComponent {
  private readonly timeline = inject(TaskTimelinePollService);
  readonly receipt = computed(() => [...this.timeline.events()].reverse().find(event =>
    event.kind === TIMELINE_KIND.steeringFeedback && event.details?.['current'] === 'true') ?? null);
  readonly tone = computed(() => steeringReceiptTone(this.receipt()?.details?.['state']));
}
