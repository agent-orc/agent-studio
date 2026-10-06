import { ChangeDetectionStrategy, Component, effect, ElementRef, inject, viewChild } from '@angular/core';

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
  private readonly region = viewChild.required<ElementRef<HTMLElement>>('region');

  constructor() {
    effect(() => {
      const event = this.alarms.announcementEvent();
      const element = this.region().nativeElement;
      if (!event) {
        element.textContent = '';
      } else if (element.textContent === event.text) {
        // Equal wording in a new cycle needs a new live-region mutation.
        element.textContent = '';
        queueMicrotask(() => {
          if (this.alarms.announcementEvent()?.id === event.id) element.textContent = event.text;
        });
      } else {
        element.textContent = event.text;
      }
    });
  }
}
