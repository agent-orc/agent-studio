import { WikiTreeMetadata, WikiTreeNode } from '../../../../models/project-docs.model';
import type { StudioIconName } from '@studio/ui/components/studio-icon/studio-icon.component';

/** Visual tone of a tree metric chip (drift grade / direction). */
export type WikiMetricTone = 'good' | 'info' | 'warn' | 'bad' | 'muted';

/** One compact per-document rating chip rendered in the wiki tree. */
export interface WikiMetricChip {
  key: string;
  icon: StudioIconName;
  display: string;
  label: string;
  tone: WikiMetricTone;
  tooltip: string;
  reportAnchor: string | null;
}

/**
 * Compact rating chips for a tree document node, derived from its companion
 * metadata: the drift grade and the temporal direction. A node without
 * companion metadata gets a single muted "unscored" chip. Pure - extracted
 * from the wiki section component so the chips stay unit-testable and the
 * component budget stays honest.
 */
export function documentMetricChips(node: WikiTreeNode): WikiMetricChip[] {
  const meta = node.metadata ?? null;
  if (!meta) {
    return [
      {
        key: 'unscored',
        icon: 'file',
        display: 'None',
        label: 'Metadata unscored',
        tone: 'muted',
        tooltip: 'No adjacent companion metadata file describes this document yet.',
        reportAnchor: null,
      },
    ];
  }

  return [driftChip(meta), directionChip(meta.temporalState)];
}

/** The drift chip for a document's companion metadata. */
export function driftChip(meta: WikiTreeMetadata): WikiMetricChip {
  const grade = cleanGrade(meta.driftGrade);
  const summary = companionTooltipSummary(meta);
  if (meta.hasDrift === false) {
    return {
      key: 'drift',
      icon: 'check',
      display: grade ?? 'A',
      label: grade ? `Drift ${grade}` : 'Drift stable',
      tone: 'good',
      tooltip: joinTooltip('No drift is currently suspected.', summary),
      reportAnchor: 'why-drift',
    };
  }
  if (meta.hasDrift === true) {
    return {
      key: 'drift',
      icon: 'diff',
      display: grade ?? '?',
      label: grade ? `Drift ${grade}` : 'Drift unknown grade',
      tone: grade === 'D' ? 'bad' : 'warn',
      tooltip: joinTooltip('Drift is suspected for this document.', summary),
      reportAnchor: 'why-drift',
    };
  }
  return {
    key: 'drift',
    icon: 'diff',
    display: grade ?? '?',
    label: grade ? `Drift ${grade}` : 'Drift unknown',
    tone: 'muted',
    tooltip: joinTooltip('Drift state is not classified yet.', summary),
    reportAnchor: 'why-drift',
  };
}

/** The temporal-direction chip (current / future / past / mixed / unknown). */
function directionChip(state: string | null): WikiMetricChip {
  const base = {
    key: 'direction',
    tone: 'muted' as WikiMetricTone,
    reportAnchor: 'temporal-reasoning',
  };
  switch (normalizeMetric(state)) {
    case 'present':
    case 'current':
    case 'now':
      return {
        ...base, icon: 'activity', display: 'Now', label: 'Direction Current',
        tooltip: 'Direction: describes current behavior.',
      };
    case 'future':
    case 'planned':
    case 'vision':
      return {
        ...base, icon: 'branch', display: 'Fut', label: 'Direction Future',
        tooltip: 'Direction: describes planned or future behavior.',
      };
    case 'past':
    case 'historic':
    case 'obsolete':
      return {
        ...base, icon: 'archive', display: 'Past', label: 'Direction Past',
        tooltip: 'Direction: describes past or obsolete behavior.',
      };
    case 'mixed':
    case 'transition':
      return {
        ...base, icon: 'diff', display: 'Mix', label: 'Direction Mixed',
        tooltip: 'Direction: mixes current and planned behavior.',
      };
    default:
      return {
        ...base, icon: 'activity', display: '?', label: 'Direction unknown',
        tooltip: 'Direction has not been classified yet.',
      };
  }
}

function cleanGrade(grade: string | null): string | null {
  const clean = grade?.trim().toUpperCase();
  return clean && /^[A-D]$/.test(clean) ? clean : null;
}

function normalizeMetric(value: string | null): string {
  return value?.trim().toLowerCase() ?? '';
}

function joinTooltip(primary: string, summary: string | null): string {
  const clean = summary?.trim();
  return clean ? `${primary} ${clean}` : primary;
}

function companionTooltipSummary(meta: WikiTreeMetadata): string | null {
  const parts: string[] = [];
  if (meta.sourceChangedSinceReview === true) {
    parts.push('Source changed since the companion review.');
  }
  if (meta.summary?.trim()) parts.push(meta.summary.trim());
  if (meta.findingsCount && meta.findingsCount > 0) {
    parts.push(`${meta.findingsCount} finding${meta.findingsCount === 1 ? '' : 's'} in the companion report.`);
  }
  if (meta.companionPath?.trim()) parts.push(`Companion: ${meta.companionPath.trim()}.`);
  return parts.length ? parts.join(' ') : null;
}
