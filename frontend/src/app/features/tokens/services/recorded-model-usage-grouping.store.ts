import { Injectable, signal } from '@angular/core';

/** Default share of the panel's total tokens below which a model folds into "Other". */
export const DEFAULT_MODEL_USAGE_GROUPING_THRESHOLD = 0.01;

interface GroupingPreference {
  thresholdShare: number;
  expanded: boolean;
}

/**
 * Per-viewer preference for the "Recorded model usage" grouping in the CLI
 * usage modal: the token-share threshold below which a model folds into the
 * "Other (n models)" row, and whether that row is left expanded. Keyed by
 * CLI type since each modal instance groups a different model set.
 *
 * Persisted to localStorage (same pattern as `DossierSectionStateService`)
 * so a reload does not silently re-collapse a row the viewer opened to
 * inspect a pricing gap.
 */
@Injectable({ providedIn: 'root' })
export class RecordedModelUsageGroupingStore {
  private readonly prefs = signal<Record<string, GroupingPreference>>({});

  threshold(scope: string): number {
    return (this.prefs()[scope] ?? readPref(scope)).thresholdShare;
  }

  expanded(scope: string): boolean {
    return (this.prefs()[scope] ?? readPref(scope)).expanded;
  }

  setThreshold(scope: string, thresholdShare: number): void {
    const current = this.prefs()[scope] ?? readPref(scope);
    this.store(scope, { ...current, thresholdShare: clampThreshold(thresholdShare) });
  }

  toggleExpanded(scope: string): void {
    const current = this.prefs()[scope] ?? readPref(scope);
    this.store(scope, { ...current, expanded: !current.expanded });
  }

  private store(scope: string, pref: GroupingPreference): void {
    this.prefs.update(p => ({ ...p, [scope]: pref }));
    try {
      globalThis.localStorage?.setItem(storageKey(scope), JSON.stringify(pref));
    } catch {
      /* Storage can be unavailable or full; the in-memory preference still works. */
    }
  }
}

function storageKey(scope: string): string {
  return `recorded-model-usage-grouping:${scope}`;
}

function clampThreshold(share: number): number {
  if (!Number.isFinite(share)) return DEFAULT_MODEL_USAGE_GROUPING_THRESHOLD;
  return Math.min(0.5, Math.max(0, share));
}

function readPref(scope: string): GroupingPreference {
  try {
    const parsed = JSON.parse(globalThis.localStorage?.getItem(storageKey(scope)) ?? 'null') as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return defaultPref();
    const record = parsed as Record<string, unknown>;
    return {
      thresholdShare: typeof record['thresholdShare'] === 'number'
        ? clampThreshold(record['thresholdShare'])
        : DEFAULT_MODEL_USAGE_GROUPING_THRESHOLD,
      expanded: record['expanded'] === true,
    };
  } catch {
    return defaultPref();
  }
}

function defaultPref(): GroupingPreference {
  return { thresholdShare: DEFAULT_MODEL_USAGE_GROUPING_THRESHOLD, expanded: false };
}
