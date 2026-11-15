import type { WikiFileSaveResult } from '../../../../models/project-docs.model';

export type WikiViewerTab = 'doc' | 'report' | 'source' | 'edit';

/** Per-project wiki layout and selection persisted in localStorage. */
export interface WikiPersistedState {
  navCollapsed?: boolean;
  openedRel?: string | null;
  viewerTab?: WikiViewerTab;
  navWidth?: number;
  contextWidth?: number;
  expandedIds?: string[];
}

export function wikiSafeViewerTab(value: unknown): WikiViewerTab {
  return value === 'source' || value === 'doc' || value === 'report' || value === 'edit'
    ? value
    : 'doc';
}

export function wikiSafeReportAnchor(anchor: string | null): string | null {
  if (!anchor) return null;
  const clean = anchor.trim().toLowerCase();
  return /^[a-z0-9-]+$/.test(clean) ? clean : null;
}

export function wikiReadStoredExpandedIds(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0);
}

/** Visible save result text: "Committed on <branch> at <sha>" or "Saved". */
export function wikiSaveResultLabel(saved: WikiFileSaveResult): string {
  let label = saved.changed ? 'Committed' : 'Saved';
  if (saved.branch) label += ` on ${saved.branch}`;
  if (saved.sha) label += ` at ${saved.sha.slice(0, 7)}`;
  return label;
}
