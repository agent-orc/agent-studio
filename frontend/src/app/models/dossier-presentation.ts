import type { StudioIconName } from '../components/studio-icon/studio-icon.component';
import type { StatusTone } from '../components/status-indicator/status-indicator.component';
import type { ArticlePattern, WorkbenchStatus } from './project-docs.model';

/**
 * One name and one glyph per Dossier state, from one source.
 *
 * The Dossier list, its cards, and the reference chip describe one lifecycle in
 * one vocabulary; before AGT-2812 each of them carried its own copy of the
 * wording, so a chip could disagree with the list it links into. The Explorer
 * rail deliberately keeps its own shorter wording for the `decided` group.
 */
export interface DossierPresentationInput {
  status: WorkbenchStatus | string;
  phase?: string | null;
  valid?: boolean;
  pattern?: ArticlePattern | null;
}

export function dossierStatusLabel(item: DossierPresentationInput): string {
  if (item.valid === false) return 'Needs attention';
  switch (item.status) {
    case 'decision-pending': return 'Decision pending';
    case 'active': return item.phase ?? 'Active';
    case 'decided': return 'Accepted / In progress';
    case 'documented': return 'Documented';
    case 'archived': return 'Discarded';
    default: return item.status;
  }
}

const DOSSIER_STATUS_TONES: Readonly<Record<string, StatusTone>> = {
  'decision-pending': 'warning',
  active: 'info',
  decided: 'success',
  documented: 'success',
  archived: 'neutral',
  invalid: 'danger',
};

/**
 * One status tone per Dossier state (AGT-3403), shared by the reference chip,
 * the Explorer rail, the overview card and the viewer header. An invalid
 * Dossier needs attention whatever its status; an unknown status reads neutral.
 */
export function dossierStatusTone(item: Pick<DossierPresentationInput, 'status' | 'valid'>): StatusTone {
  if (item.valid === false) return 'danger';
  return DOSSIER_STATUS_TONES[item.status] ?? 'neutral';
}

/** Presentation-variant glyph: UI dossiers read as a surface, concepts as a document. */
export function dossierPatternIcon(pattern: ArticlePattern | null | undefined): StudioIconName {
  return pattern === 'ui' ? 'grid' : 'book';
}
