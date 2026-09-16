import type { StudioIconName } from '../components/studio-icon/studio-icon.component';
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

/** Presentation-variant glyph: UI dossiers read as a surface, concepts as a document. */
export function dossierPatternIcon(pattern: ArticlePattern | null | undefined): StudioIconName {
  return pattern === 'ui' ? 'grid' : 'book';
}
