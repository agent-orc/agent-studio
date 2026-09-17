/**
 * Rows of the header Dev-tools menu.
 *
 * AGT-2819: the list used to be built inline in the `App` shell, which meant
 * the only way to check that a disabled flag hides both its row and the section
 * header it introduces was to mount the whole application. It is a pure
 * function of the flags, so it lives next to the dev-tools feature it describes.
 */
import type { MenuItem } from '../../components/menu';
import type { DevToolsFlags } from '../../services/dev-tools.service';

/** Ids the shell dispatches on; the menu never invents an id the shell cannot route. */
export const DEVTOOLS_MENU_IDS = {
  orchestratorConfig: 'orch-config',
  tagManager: 'tag-manager',
  updateStable: 'update-stable',
  deleteE2E: 'delete-e2e',
} as const;

/**
 * F23: typed menu-item list driving the shared `<app-menu>` in the header.
 * Replaced the inline button-per-row markup that lived directly in `app.html`
 * (and its companion `.devtools-menu*` SCSS block).
 *
 * The "Dev tools" section header only appears when at least one flag below it
 * is on, so an operator checkout never sees an empty section.
 */
export function buildDevtoolsMenuItems(flags: DevToolsFlags): MenuItem[] {
  const items: MenuItem[] = [
    { kind: 'header', label: 'System' },
    {
      kind: 'row',
      id: DEVTOOLS_MENU_IDS.orchestratorConfig,
      label: 'Orchestrator config',
      hint: 'supervisor + meta-cycle flags',
    },
    {
      kind: 'row',
      id: DEVTOOLS_MENU_IDS.tagManager,
      label: 'Tag manager',
      hint: 'add, edit, and remove registry tags',
    },
  ];
  if (flags.updateStableEnabled || flags.deleteE2EJobsEnabled) {
    items.push({ kind: 'header', label: 'Dev tools' });
  }
  if (flags.updateStableEnabled) {
    items.push({
      kind: 'row',
      id: DEVTOOLS_MENU_IDS.updateStable,
      label: 'Update Stable',
      hint: 'open resilient update center',
    });
  }
  if (flags.deleteE2EJobsEnabled) {
    items.push({
      kind: 'row',
      id: DEVTOOLS_MENU_IDS.deleteE2E,
      label: 'Delete E2E Tasks',
      hint: 'across all projects',
      danger: true,
    });
  }
  return items;
}
