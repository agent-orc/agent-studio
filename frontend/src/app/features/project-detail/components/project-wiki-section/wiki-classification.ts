import { WikiClassification } from '../../../../models/project-docs.model';
import type { StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Visual tone of a classification badge in the tree / folder table. */
export type WikiClassBadgeTone = 'stale' | 'superseded' | 'muted';

/**
 * Shared status-indicator tone for a classification status (AGT-3403):
 * veraltet is a danger, überholt needs attention, archived and any other raw
 * status stay neutral.
 */
const CLASS_BADGE_STATUS_TONES: Readonly<Record<WikiClassBadgeTone, StatusTone>> = {
  stale: 'danger',
  superseded: 'warning',
  muted: 'neutral',
};

export function classificationStatusTone(tone: WikiClassBadgeTone | null | undefined): StatusTone {
  return (tone && CLASS_BADGE_STATUS_TONES[tone]) || 'neutral';
}

/** One compact classification badge (status chip or 2-3-letter type code). */
export interface WikiClassBadge {
  key: 'status' | 'type';
  label: string;
  tone: WikiClassBadgeTone;
  tooltip: string;
}

/** 2-3-letter codes for the curated document types shown in the tree. */
const TYPE_ABBREVIATIONS: Readonly<Record<string, string>> = {
  konzept: 'KON',
  adr: 'ADR',
  contract: 'CTR',
  'domain-map': 'DOM',
  analyse: 'ANA',
  runbook: 'RUN',
  workbench: 'WB',
  mockup: 'MCK',
  proposal: 'PRP',
  generiert: 'GEN',
  index: 'IDX',
};

/** Human labels for the curated document types (tooltip copy). */
const TYPE_LABELS: Readonly<Record<string, string>> = {
  konzept: 'Konzept',
  adr: 'ADR',
  contract: 'Contract',
  'domain-map': 'Domain-Map',
  analyse: 'Analyse',
  runbook: 'Runbook',
  workbench: 'Dossier',
  mockup: 'Mockup',
  proposal: 'Proposal',
  generiert: 'Generiert',
  index: 'Index',
};

/** `2026-07-18` -> `18.07.2026` (falls back to the raw value). */
export function formatAnalyzedDate(iso: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso.trim());
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso.trim();
}

/** 2-3-letter code for a curated type; unknown types shorten to 3 letters. */
export function classificationTypeAbbreviation(type: string): string {
  const clean = type.trim().toLowerCase();
  return TYPE_ABBREVIATIONS[clean] ?? clean.slice(0, 3).toUpperCase();
}

/** Full human label for a curated type (`domain-map` -> `Domain-Map`); raw fallback. */
export function classificationTypeLabel(type: string): string {
  const clean = type.trim();
  return TYPE_LABELS[clean.toLowerCase()] ?? clean;
}

/** Status chip of the meta-panel classification block. */
export interface WikiClassMetaStatus {
  label: string;
  tone: WikiClassBadgeTone;
  /** Docs-relative successor page when the status is `ueberholt`. */
  supersededBy: string | null;
}

/** View model of the meta-panel "Klassifikation" block. */
export interface WikiClassMeta {
  status: WikiClassMetaStatus | null;
  /** Spelled-out type label (not the compact tree code). */
  typeLabel: string | null;
  /** Analysis date pre-formatted as dd.mm.yyyy. */
  analyzedAt: string | null;
}

/**
 * Meta-rail view of a page's classification: unlike the compact tree badges,
 * the block spells the type out, shows the analysis date as visible text, and
 * carries the successor path so the template can render it as a navigable
 * link. `aktuell` (and any unknown status) renders as a quiet muted chip here
 * - the tree hides it, but the meta panel is exactly the place to state it.
 * Null when the page carries no classification data at all (block hidden).
 */
export function classificationMeta(
  classification: WikiClassification | null | undefined,
): WikiClassMeta | null {
  if (!classification) return null;

  const rawStatus = classification.status?.trim();
  let status: WikiClassMetaStatus | null = null;
  if (rawStatus?.toLowerCase() === 'veraltet') {
    status = { label: 'veraltet', tone: 'stale', supersededBy: null };
  } else if (rawStatus?.toLowerCase() === 'ueberholt') {
    status = {
      label: 'überholt',
      tone: 'superseded',
      supersededBy: classification.supersededBy?.trim() || null,
    };
  } else if (rawStatus?.toLowerCase() === 'archived') {
    status = { label: 'Archived', tone: 'muted', supersededBy: null };
  } else if (rawStatus) {
    status = { label: rawStatus, tone: 'muted', supersededBy: null };
  }

  const type = classification.type?.trim();
  const typeLabel = type ? classificationTypeLabel(type) : null;
  const analyzedAtRaw = classification.analyzedAt?.trim();
  const analyzedAt = analyzedAtRaw ? formatAnalyzedDate(analyzedAtRaw) : null;

  if (!status && !typeLabel && !analyzedAt) return null;
  return { status, typeLabel, analyzedAt };
}

function analyzedSuffix(classification: WikiClassification): string {
  const at = classification.analyzedAt?.trim();
  return at ? ` · Analyse ${formatAnalyzedDate(at)}` : '';
}

/**
 * Compact classification badges for a page row: a muted status chip
 * (`veraltet` / `überholt`; `aktuell` and unclassified render nothing) plus a
 * muted 2-3-letter type code. The analysis date lives in the tooltips only, so
 * the tree keeps its narrow-width budget. Pure - shared by the wiki tree rows
 * and the folder-overview table.
 */
export function classificationBadges(
  classification: WikiClassification | null | undefined,
): WikiClassBadge[] {
  if (!classification) return [];
  const badges: WikiClassBadge[] = [];

  const status = classification.status?.trim().toLowerCase();
  if (status === 'veraltet') {
    badges.push({
      key: 'status',
      label: 'veraltet',
      tone: 'stale',
      tooltip: `Veraltet: Inhalt ist nicht mehr aktuell.${analyzedSuffix(classification)}`,
    });
  } else if (status === 'ueberholt') {
    const successor = classification.supersededBy?.trim();
    badges.push({
      key: 'status',
      label: 'überholt',
      tone: 'superseded',
      tooltip: successor
        ? `Überholt durch ${successor}.${analyzedSuffix(classification)}`
        : `Überholt.${analyzedSuffix(classification)}`,
    });
  } else if (status === 'archived') {
    badges.push({
      key: 'status',
      label: 'archived',
      tone: 'muted',
      tooltip: `Archived page retained as history.${analyzedSuffix(classification)}`,
    });
  }

  const type = classification.type?.trim();
  if (type) {
    const label = TYPE_LABELS[type.toLowerCase()] ?? type;
    badges.push({
      key: 'type',
      label: classificationTypeAbbreviation(type),
      tone: 'muted',
      tooltip: `Typ: ${label}.${analyzedSuffix(classification)}`,
    });
  }

  return badges;
}
