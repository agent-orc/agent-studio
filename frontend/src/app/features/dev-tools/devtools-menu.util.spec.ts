/**
 * AGT-2819: pins the Dev-tools menu behaviour the `App` shell used to carry
 * inline, so the extraction is provably behaviour-preserving.
 */
import { describe, expect, it } from 'vitest';
import { buildDevtoolsMenuItems, DEVTOOLS_MENU_IDS } from './devtools-menu.util';

const allOff = { updateStableEnabled: false, deleteE2EJobsEnabled: false };

function ids(items: ReturnType<typeof buildDevtoolsMenuItems>): (string | undefined)[] {
  return items.map(item => {
    if (item.kind === 'row') return item.id;
    return item.kind === 'header' ? `header:${item.label}` : 'separator';
  });
}

describe('buildDevtoolsMenuItems', () => {
  it('always offers the System rows', () => {
    expect(ids(buildDevtoolsMenuItems(allOff))).toEqual([
      'header:System',
      DEVTOOLS_MENU_IDS.orchestratorConfig,
      DEVTOOLS_MENU_IDS.tagManager,
    ]);
  });

  it('omits the Dev tools section header while both flags are off', () => {
    expect(ids(buildDevtoolsMenuItems(allOff))).not.toContain('header:Dev tools');
  });

  it('introduces the Dev tools section as soon as one flag is on', () => {
    expect(ids(buildDevtoolsMenuItems({ ...allOff, updateStableEnabled: true }))).toEqual([
      'header:System',
      DEVTOOLS_MENU_IDS.orchestratorConfig,
      DEVTOOLS_MENU_IDS.tagManager,
      'header:Dev tools',
      DEVTOOLS_MENU_IDS.updateStable,
    ]);
  });

  it('renders both dev rows in flag order when both are on', () => {
    expect(ids(buildDevtoolsMenuItems({ updateStableEnabled: true, deleteE2EJobsEnabled: true })))
      .toEqual([
        'header:System',
        DEVTOOLS_MENU_IDS.orchestratorConfig,
        DEVTOOLS_MENU_IDS.tagManager,
        'header:Dev tools',
        DEVTOOLS_MENU_IDS.updateStable,
        DEVTOOLS_MENU_IDS.deleteE2E,
      ]);
  });

  it('marks the destructive E2E cleanup row as danger and nothing else', () => {
    const items = buildDevtoolsMenuItems({
      updateStableEnabled: true,
      deleteE2EJobsEnabled: true,
    });
    const danger = items.filter(item => item.kind === 'row' && item.danger);
    expect(danger).toHaveLength(1);
    expect(danger[0]).toMatchObject({ id: DEVTOOLS_MENU_IDS.deleteE2E });
  });
});
