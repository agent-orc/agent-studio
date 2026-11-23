import { computed, inject, type Signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { AcceptanceReadinessStore } from '../../../acceptance-readiness/state/acceptance-readiness.store';
import { AcceptanceRefusalStore, acceptanceRefusalFor, contentRefusalReason } from '../../state/acceptance-refusal.model';
import type { TriageButton } from '../../state/triage-actions.model';

/**
 * AGT-3830: the reason a content refusal (review concerns, no content verdict,
 * or a refused click) holds the header's Accept, or null. Call in an injection
 * context; the Studio toolbar offers the refusal's next step.
 */
export function contentRefusalOf(info: () => TaskInfo, primary: () => TriageButton | null): Signal<string | null> {
  const readiness = inject(AcceptanceReadinessStore);
  const refusals = inject(AcceptanceRefusalStore);
  return computed(() => primary()?.id !== 'mark-done' ? null : contentRefusalReason(
    acceptanceRefusalFor(info(), refusals.forCard(info()), false, readiness.itemFor(info()))));
}
