import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StudioIconComponent } from '@studio/ui/components/studio-icon/studio-icon.component';
import { AppTooltipDirective } from '@studio/ui/components/tooltip/app-tooltip.directive';

@Component({
  selector: 'app-host-workload-summary',
  standalone: true,
  imports: [StudioIconComponent, AppTooltipDirective],
  templateUrl: './host-workload-summary.html',
  styleUrl: './host-workload-summary.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostWorkloadSummaryComponent {
  readonly runSlots = input.required<string>();
  readonly runSlotsStale = input(false);
  readonly runSlotsDiverge = input(false);
  readonly runSlotsTooltip = input('');
}
