/**
 * Wire shape of `GET /api/projects/{p}/deploy/lanes`, the read model of the
 * board's Delivery section (AGT-3276 D5 L-B, `DeliveryColumnProjection`).
 * The first column is `integrated`; every other column is one environment in
 * lifecycle order. Card lane states never change; the section only places
 * cards by environment.
 */
export interface DeliveryColumnCard {
  key: string;
  releaseId: string | null;
  releaseVersion: string | null;
  rolledBack: boolean;
}

export interface DeliveryColumnRelease {
  id: string;
  version: string;
  channel: string;
  createdAtUtc: string;
}

export type DeliveryRunState =
  | 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled' | 'awaiting-approval' | 'waiting-for-intervention';

export interface DeliveryColumnRun {
  id: string;
  releaseId: string;
  state: DeliveryRunState | string;
  trigger: string;
  atUtc: string | null;
}

export interface DeliveryColumnApproval {
  deploymentId: string;
  releaseId: string;
  releaseVersion: string | null;
  requestedAtUtc: string;
}

export interface DeliveryColumn {
  id: string;
  environmentId: string | null;
  name: string;
  kind: string | null;
  order: number;
  currentRelease: DeliveryColumnRelease | null;
  lastRun: DeliveryColumnRun | null;
  health: string | null;
  pendingApproval: DeliveryColumnApproval | null;
  landed: DeliveryColumnCard[];
  waiting: DeliveryColumnCard[];
  rolledBack: DeliveryColumnCard[];
}

export interface DeliveryColumnsResponse {
  projectId: string;
  releaseDepth: number;
  columns: DeliveryColumn[];
}

/** Id of the read model's first column: integrated, in no release yet. */
export const INTEGRATED_DELIVERY_COLUMN_ID = 'integrated';
