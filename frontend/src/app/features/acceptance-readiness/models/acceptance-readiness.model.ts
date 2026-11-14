import type { StatusTone } from '../../../components/status-indicator/status-indicator.component';

/** Readiness classes of `POST /api/tasks/acceptance-readiness` (AGT-3564). */
export type AcceptanceReadinessClass =
  | 'ready'
  | 'review-concerns'
  | 'integrated-unverified'
  | 'not-integrated'
  | 'verdict-stale'
  | 'no-content-verdict'
  | 'blocked'
  | 'waiting-deploy'
  | 'owner-decision';

/** The prefilled continue round of a refused card; sent only after the owner confirms. */
export interface AcceptanceReadinessRound {
  prompt: string;
  baseRef: string | null;
}

/** One Human Review card and the answer its acceptance would give now. */
export interface AcceptanceReadinessItem {
  jobId: string;
  taskKey: string;
  key: string | null;
  projectName: string;
  title: string;
  readiness: AcceptanceReadinessClass;
  headline: string;
  reason: string;
  stage: string;
  refusal: string;
  aspect: string | null;
  contentStatus: string | null;
  concern: string | null;
  missing: string | null;
  latestResultRef: string | null;
  round: AcceptanceReadinessRound | null;
}

export interface AcceptanceReadinessReport {
  checkedAtUtc: string;
  counts: Record<AcceptanceReadinessClass, number>;
  items: AcceptanceReadinessItem[];
}

/** Lane filter: every card grouped by class, only ready cards, or the rest. */
export type AcceptanceReadinessFilter = 'all' | 'ready' | 'not-ready';

/** Per-card outcome of "Accept all ready", from the normal batch move. */
export interface AcceptanceResult {
  jobId: string;
  title: string;
  accepted: boolean;
  message: string;
}

interface ReadinessPresentation {
  readonly label: string;
  readonly tone: StatusTone;
}

/** Group heading and status tone per class, in display order. */
export const READINESS_PRESENTATION: Readonly<Record<AcceptanceReadinessClass, ReadinessPresentation>> = {
  'ready': { label: 'Ready to accept', tone: 'success' },
  'review-concerns': { label: 'Review concerns', tone: 'warning' },
  'integrated-unverified': { label: 'Integrated, not verified', tone: 'warning' },
  'not-integrated': { label: 'Not integrated', tone: 'warning' },
  'verdict-stale': { label: 'Verdict stale', tone: 'warning' },
  'no-content-verdict': { label: 'No content verdict', tone: 'warning' },
  'blocked': { label: 'Completion refused', tone: 'danger' },
  'waiting-deploy': { label: 'Waiting for a deploy', tone: 'info' },
  'owner-decision': { label: 'Owner decision', tone: 'info' },
};

export const READINESS_ORDER = Object.keys(READINESS_PRESENTATION) as AcceptanceReadinessClass[];

/** Unknown classes from a newer backend read as a plain refusal. */
export function readinessPresentation(readiness: string): ReadinessPresentation {
  return READINESS_PRESENTATION[readiness as AcceptanceReadinessClass] ?? READINESS_PRESENTATION.blocked;
}
