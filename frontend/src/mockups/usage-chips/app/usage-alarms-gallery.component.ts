import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';

import {
  UsageAlarmStateService,
  UsageAlarmStatusComponent,
  UsageCliChipComponent,
  UsageCostChipComponent,
} from '../../../app/features/usage-cockpit';
import * as A from './usage-alarms.fixtures';

/**
 * HUC-S5 fixture harness (`?view=alarms`). Mounts the real chips with alarm
 * states latched by the real policy, a phone-width row with a hidden-provider
 * alarm, and a scripted refresh sequence that drives the real
 * `UsageAlarmStateService` and polite status region. Not the HUC-S4 header.
 */
@Component({
  selector: 'mockup-usage-alarms-gallery',
  standalone: true,
  imports: [UsageCliChipComponent, UsageCostChipComponent, UsageAlarmStatusComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-alarms-gallery.component.html',
  styleUrl: './usage-chips-gallery.component.scss',
})
export class UsageAlarmsGalleryComponent {
  readonly a = A;
  readonly theme = new URLSearchParams(location.search).get('theme') === 'light' ? 'light' : 'dark';
  readonly alarms = inject(UsageAlarmStateService);

  /** Index of the last ingested transition step; -1 before the first refresh. */
  readonly step = signal(-1);
  readonly current = computed(() => A.TRANSITIONS[Math.max(0, this.step())]);
  readonly stepCaption = computed(() => this.step() < 0
    ? 'No snapshot yet'
    : `${this.step() + 1}/${A.TRANSITIONS.length}: ${this.current().label}`);
  readonly codex = computed(() => this.step() < 0 ? null : this.current().snapshot.clis[0]);
  readonly codexAlarms = computed(() => {
    this.alarms.state();
    return this.alarms.alarmsFor('cli:codex');
  });
  /** Visible copy of what the polite region said, for the evidence only. */
  readonly spokenLog = signal<string[]>([]);

  constructor() {
    document.documentElement.setAttribute('data-studio-theme', this.theme);
    let lastId = 0;
    effect(() => {
      const event = this.alarms.announcementEvent();
      if (event && event.id !== lastId) this.spokenLog.update(log => [...log, event.text]);
      lastId = event?.id ?? 0;
    });
  }

  refresh(): void {
    const next = this.step() + 1;
    if (next >= A.TRANSITIONS.length) return;
    const t = A.TRANSITIONS[next];
    this.alarms.ingest(t.snapshot, t.at);
    this.step.set(next);
  }
}
