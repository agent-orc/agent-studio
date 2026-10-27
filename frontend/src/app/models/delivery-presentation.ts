import { INTEGRATED_DELIVERY_COLUMN_ID, type DeliveryColumn, type DeliveryColumnRun } from './delivery-column.model';

/**
 * Delivery presentation: the single source for how the board's Delivery
 * section (AGT-3276 D5 L-B) names and tints its columns, the sibling of
 * `lane-presentation.ts`. The section is a projection by environment, not a
 * set of lanes, so it keeps its words here rather than in the lane catalogue;
 * a card in it stays in its lane (6-completed, "Accepted" since D10 A).
 *
 * Bind `[attr.data-delivery-tone]` and style from the tone tokens; no
 * component keeps its own delivery string or colour rule.
 */
export type DeliveryTone = 'ok' | 'active' | 'attention' | 'danger' | 'neutral';

export interface DeliveryLabel {
  readonly label: string;
  readonly tone: DeliveryTone;
}

export const DELIVERY_COPY = {
  groupLabel: 'Delivery',
  groupSentence: 'Where accepted cards run, by environment. Derived from releases and deploy runs.',
  integratedTitle: 'Integrated',
  integratedSubtitle: 'In no release yet',
  noRelease: 'No release yet',
  waiting: 'Waiting',
  landedEmpty: 'No card landed',
  approvalPending: 'Approval pending',
  rolledBack: 'Rolled back',
  furthestOnly: 'Furthest environment only',
  collapse: 'Collapse column',
  expand: 'Expand column',
  loadError: 'Some delivery data could not be loaded.',
} as const;

const RUN_STATES: Readonly<Record<string, DeliveryLabel>> = {
  queued: { label: 'Queued', tone: 'active' },
  running: { label: 'Running', tone: 'active' },
  succeeded: { label: 'Succeeded', tone: 'ok' },
  failed: { label: 'Failed', tone: 'danger' },
  cancelled: { label: 'Cancelled', tone: 'neutral' },
  'awaiting-approval': { label: DELIVERY_COPY.approvalPending, tone: 'attention' },
  'waiting-for-intervention': { label: 'Needs intervention', tone: 'danger' },
};

const HEALTH: Readonly<Record<string, DeliveryLabel>> = {
  healthy: { label: 'Healthy', tone: 'ok' },
  unhealthy: { label: 'Unhealthy', tone: 'danger' },
  unknown: { label: 'Health unknown', tone: 'neutral' },
};

/** Column title: "Integrated" for the first column, "Delivery to <environment>" otherwise. */
export function deliveryColumnTitle(column: Pick<DeliveryColumn, 'id' | 'name'>): string {
  return column.id === INTEGRATED_DELIVERY_COLUMN_ID ? DELIVERY_COPY.integratedTitle : `Delivery to ${column.name}`;
}

/** The last run's state; a succeeded rollback reads as "Rolled back". */
export function deliveryRunLabel(run: Pick<DeliveryColumnRun, 'state' | 'trigger'>): DeliveryLabel {
  if (run.state === 'succeeded' && run.trigger.endsWith('rollback')) {
    return { label: DELIVERY_COPY.rolledBack, tone: 'attention' };
  }
  return RUN_STATES[run.state] ?? { label: run.state, tone: 'neutral' };
}

export function deliveryHealthLabel(health: string | null | undefined): DeliveryLabel {
  return HEALTH[health ?? 'unknown'] ?? HEALTH['unknown'];
}
