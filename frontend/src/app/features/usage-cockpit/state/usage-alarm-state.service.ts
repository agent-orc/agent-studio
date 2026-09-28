import { Injectable, signal } from '@angular/core';

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
 * numeric tick is never re-announced.
 */
@Injectable({ providedIn: 'root' })
export class UsageAlarmStateService {
  private readonly latched = signal(EMPTY_USAGE_ALARM_STATE);
  private readonly spoken = signal('');

  readonly state = this.latched.asReadonly();
  /** Latest polite announcement text. */
  readonly announcement = this.spoken.asReadonly();

  ingest(snapshot: UsageCockpitResponse, now: number = Date.now()): void {
    const step = reduceUsageAlarms(this.latched(), snapshot.workspaceId, observeSnapshot(snapshot, now), now);
    this.latched.set(step.state);
    if (step.announcements.length > 0) this.spoken.set(step.announcements.join(' '));
  }

  alarmsFor(source: string): UsageAlarm[] {
    return alarmsForSource(this.latched(), source);
  }

  hiddenAlarms(visibleCliIds: readonly string[]): UsageAlarm[] {
    return hiddenProviderAlarms(this.latched(), visibleCliIds);
  }

  reset(): void {
    this.latched.set(EMPTY_USAGE_ALARM_STATE);
    this.spoken.set('');
  }
}
