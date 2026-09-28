import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { UsageAlarmStateService } from '../../state/usage-alarm-state.service';

/**
 * Polite status region for usage alarm transitions (HUC-S5). Mount it once,
 * next to the usage strip, before the first snapshot arrives so assistive
 * technology registers the region. It renders no visible content.
 */
@Component({
  selector: 'app-usage-alarm-status',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-alarm-status.html',
  styleUrl: './usage-alarm-status.scss',
})
export class UsageAlarmStatusComponent {
  readonly alarms = inject(UsageAlarmStateService);
}
