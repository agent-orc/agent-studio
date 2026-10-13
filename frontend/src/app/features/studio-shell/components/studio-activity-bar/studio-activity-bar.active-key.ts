import type { StudioPanelKind, StudioTabKind } from '../../studio-shell.types';

/**
 * Every item the ActivityBar can mark as active. This is the union of the
 * sidebar-panel kinds (`explorer` … `settings`) and the editor destinations
 * that own their own rail button (`epics`). `settings` is shared:
 * it is both a sidebar panel and the target of the workspace-settings tab.
 */
export type StudioActivityItemKey = StudioPanelKind | 'epics' | 'workbenches';

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
 * Priority — the surface the user is actually looking at wins:
 *  1. The active editor tab, when it is one of the ActivityBar destinations
 *     (Epics / Workspace settings). The main pane is the shown
 *     surface, so its marker takes precedence over a merely-open sidebar.
 *  2. Otherwise the open sidebar panel (only while the sidebar is visible).
 *  3. Otherwise nothing is active.
 */
export function resolveActiveActivityKey(state: {
  activeTabKind: StudioTabKind | undefined;
  activePanel: StudioPanelKind;
  sidebarVisible: boolean;
}): StudioActivityItemKey | null {
  switch (state.activeTabKind) {
    case 'feed':
      return 'activity';
    case 'epics':
      return 'epics';
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
