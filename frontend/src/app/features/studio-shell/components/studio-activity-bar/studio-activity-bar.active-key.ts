import type { StudioPanelKind, StudioTabKind } from '../../studio-shell.types';

/**
 * Every item the ActivityBar can mark as active. This is the union of the
 * sidebar-panel kinds (`explorer` … `settings`) and the editor destinations
 * that own their own rail button (`workbenches`). `settings` is shared:
 * it is both a sidebar panel and the target of the workspace-settings tab.
 */
export type StudioActivityItemKey = StudioPanelKind | 'workbenches';

/**
 * The bug (AGT-2042): the ActivityBar drew its active marker from two
 * independent sources — the sidebar toggle (`activePanel` + `sidebarVisible`)
 * and the editor route (`activeTab().kind`). Both could be true at once
 * (e.g. Explorer sidebar open while an editor destination is active), so two buttons
 * lit up together.
 *
 * This resolver collapses those sources into exactly one key, so the marker
 * is structurally exclusive: a button is active iff its key equals the single
 * value this returns. Focus/hover rings are a separate visual concern and
 * never feed into this.
 *
 * Rule — the surface the user activated last wins (AGT-3351):
 *  1. When the sidebar panel was activated last (`markerOwner === 'panel'`,
 *     a rail click or a programmatic `StudioPanelStateService.open`) and the
 *     sidebar is visible, the open sidebar panel is marked — even while a
 *     destination tab (Dossiers / Feed / Workspace settings) is active.
 *  2. Otherwise the active editor tab, when it is one of the ActivityBar
 *     destinations.
 *  3. Otherwise the open sidebar panel (only while the sidebar is visible).
 *  4. Otherwise nothing is active.
 *
 * A missing `markerOwner` (e.g. older stored state) behaves like `'editor'`,
 * i.e. the pre-AGT-3351 priority where the destination tab wins.
 */
export type StudioActivityMarkerOwner = 'panel' | 'editor';

export function resolveActiveActivityKey(state: {
  activeTabKind: StudioTabKind | undefined;
  activePanel: StudioPanelKind;
  sidebarVisible: boolean;
  markerOwner?: StudioActivityMarkerOwner;
}): StudioActivityItemKey | null {
  if (state.sidebarVisible && state.markerOwner === 'panel') {
    return state.activePanel;
  }
  switch (state.activeTabKind) {
    case 'feed':
      return 'activity';
    case 'workbenches':
      return 'workbenches';
    case 'workspace-settings':
      return 'settings';
  }
  if (state.sidebarVisible) {
    return state.activePanel;
  }
  return null;
}
