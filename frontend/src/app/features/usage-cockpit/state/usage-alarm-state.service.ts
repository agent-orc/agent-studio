import { computed, Injectable, signal } from '@angular/core';

import type { UsageCockpitResponse } from '../models/usage-cockpit.model';
import {
  alarmsForSource,
  EMPTY_USAGE_ALARM_STATE,
  hiddenProviderAlarms,
  observeSnapshot,
  reduceUsageAlarms,
  type UsageAlarm,
} from '../usage-alarm.policy';

/**
 * Latched usage alarms for the header cockpit (HUC-S5). The host feeds every
 * cockpit snapshot through `ingest`; chips read their alarms from here and
 * `<app-usage-alarm-status>` speaks `announcement` through a polite region.
 * A refresh that changes no alarm leaves the announcement untouched, so a
 * numeric tick is never re-announced. A new transition gets a new event even
 * when its wording matches a previous reset cycle.
 */
@Injectable({ providedIn: 'root' })
export class UsageAlarmStateService {
  private readonly latched = signal(EMPTY_USAGE_ALARM_STATE);
  private readonly spoken = signal<{ id: number; text: string } | null>(null);
  private nextAnnouncementId = 0;

  readonly state = this.latched.asReadonly();
  /** Latest polite announcement text. */
  readonly announcementEvent = this.spoken.asReadonly();
  readonly announcement = computed(() => this.spoken()?.text ?? '');

  ingest(snapshot: UsageCockpitResponse, now: number = Date.now()): void {
    const step = reduceUsageAlarms(this.latched(), snapshot.workspaceId, observeSnapshot(snapshot, now), now);
    this.latched.set(step.state);
    if (step.announcements.length > 0) {
      this.spoken.set({ id: ++this.nextAnnouncementId, text: step.announcements.join(' ') });
    }
  }

  alarmsFor(source: string): UsageAlarm[] {
    return alarmsForSource(this.latched(), source);
  }

  hiddenAlarms(visibleCliIds: readonly string[]): UsageAlarm[] {
    return hiddenProviderAlarms(this.latched(), visibleCliIds);
  }

  reset(): void {
    this.latched.set(EMPTY_USAGE_ALARM_STATE);
    this.spoken.set(null);
  }
}
