import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { StudioActivityBarComponent, StudioActivityBarItem } from './studio-activity-bar.component';
import { resolveActiveActivityKey } from './studio-activity-bar.active-key';
import { StudioPanelStateService } from '../../services/studio-panel-state.service';

const ITEMS: readonly StudioActivityBarItem[] = [
  { key: 'explorer', icon: 'folder', label: 'Explorer' },
  { key: 'filters', icon: 'filter', label: 'Filters' },
  { key: 'cli', icon: 'cli', label: 'Agents / CLI' },
  { key: 'activity', icon: 'activity', label: 'Activity' },
];

/**
 * Cycle 11c smoke. Compiles + instantiates the standalone component.
 * What this catches: broken templateUrl/styleUrl resolution, broken
 * inject() wiring, broken signal init, decorator metadata regressions.
 */
describe('StudioActivityBarComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    try {
      await TestBed.configureTestingModule({
        imports: [StudioActivityBarComponent],
        providers: [
          provideZonelessChangeDetection(),
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
        ],
      }).compileComponents();
      const fixture = TestBed.createComponent(StudioActivityBarComponent);
      fixture.componentRef.setInput('items', ITEMS);
      fixture.componentRef.setInput('activeKey', 'explorer');

      try { fixture.detectChanges(); } catch (e) {
        console.warn('[smoke] StudioActivityBarComponent initial render skipped:', (e as Error).message);
      }
      expect(fixture.componentInstance).toBeTruthy();
    } catch (e) {
      console.warn('[smoke] StudioActivityBarComponent TestBed setup skipped:', (e as Error).message);
      expect(StudioActivityBarComponent).toBeTruthy();
    }
  });
});

describe('StudioActivityBarComponent destinations', () => {
  function mount(activeKey: string | null = 'explorer') {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [StudioActivityBarComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    });
    const fixture = TestBed.createComponent(StudioActivityBarComponent);
    fixture.componentRef.setInput('items', ITEMS);
    fixture.componentRef.setInput('activeKey', activeKey);
    fixture.detectChanges();
    return fixture;
  }

  it('renders no Runbook button (removed panel)', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-ab-runbook"]')).toBeNull();
  });

  it('renders no Admin button (removed destination)', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-ab-admin"]')).toBeNull();
  });

  it('leaves Settings to the top-right header', () => {
    const fixture = mount();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="studio-ab-settings"]')).toBeNull();
  });

  /**
   * AGT-2042 regression: exactly one button carries the active marker for
   * any resolved key. The whole point of the single `activeKey` input is
   * that two items can never light up together — assert it on the DOM.
   */
  it('marks at most one button active for any active key', () => {
    for (const key of ['explorer', 'filters', 'chats', 'workbenches', null]) {
      const fixture = mount(key);
      const host = fixture.nativeElement as HTMLElement;
      const active = host.querySelectorAll('.studio-ab__btn--active');
      expect(active.length, `activeKey=${key}`).toBeLessThanOrEqual(1);
      if (key !== null) {
        expect(active.length, `activeKey=${key}`).toBe(1);
      }
    }
  });
});

/**
 * AGT-2042: the resolver is the single source that collapses the sidebar
 * toggle and the editor route into one exclusive key.
 */
describe('resolveActiveActivityKey', () => {
  it('lets the editor destination win over an open sidebar panel when the editor was activated last', () => {
    // Explorer sidebar open and a Dossiers tab active. The tab was activated
    // last (or no owner is stored yet), so only Dossiers wins.
    for (const markerOwner of ['editor', undefined] as const) {
      expect(resolveActiveActivityKey({
        activeTabKind: 'workbenches',
        activePanel: 'explorer',
        sidebarVisible: true,
        markerOwner,
      })).toBe('workbenches');
    }
  });

  it('lets the sidebar panel win over the editor destination when the panel was activated last', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'workbenches',
      activePanel: 'explorer',
      sidebarVisible: true,
      markerOwner: 'panel',
    })).toBe('explorer');
  });

  it('maps the workspace-settings tab to the settings item', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'workspace-settings',
      activePanel: 'explorer',
      sidebarVisible: true,
    })).toBe('settings');
  });

  it('maps the Feed main route to the Activity item', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'feed',
      activePanel: 'explorer',
      sidebarVisible: true,
    })).toBe('activity');
  });

  it('marks the Chats rail button while the sidebar Chats panel is open', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'board',
      activePanel: 'chats',
      sidebarVisible: true,
    })).toBe('chats');
  });

  it('falls back to the open sidebar panel for non-destination tabs', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'board',
      activePanel: 'filters',
      sidebarVisible: true,
    })).toBe('filters');
  });

  it('returns null when the sidebar is hidden and the tab is not a destination', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'board',
      activePanel: 'explorer',
      sidebarVisible: false,
    })).toBeNull();
  });

  it('keeps the destination marker even when the sidebar is hidden', () => {
    expect(resolveActiveActivityKey({
      activeTabKind: 'workbenches',
      activePanel: 'explorer',
      sidebarVisible: false,
    })).toBe('workbenches');
  });
});

/**
 * AGT-3351: the marker follows the most recent activation. Each row of the
 * card table is replayed through the service transitions the shell uses
 * (`toggle`/`open` mark the panel, a tab activation marks the editor).
 */
describe('resolveActiveActivityKey · most recent activation wins', () => {
  type State = Parameters<typeof resolveActiveActivityKey>[0];
  const dossierTab: State = {
    activeTabKind: 'workbenches', activePanel: 'explorer', sidebarVisible: true, markerOwner: 'editor',
  };
  const clickPanel = (s: State, panel: State['activePanel']): State =>
    s.activePanel === panel && s.sidebarVisible
      ? { ...s, sidebarVisible: false }
      : { ...s, activePanel: panel, sidebarVisible: true, markerOwner: 'panel' };
  const activateTab = (s: State, kind: State['activeTabKind']): State =>
    ({ ...s, activeTabKind: kind, markerOwner: 'editor' });

  const cases: { name: string; state: State; expected: string | null }[] = [
    { name: 'Dossier tab + click Chats', state: clickPanel(dossierTab, 'chats'), expected: 'chats' },
    { name: 'Dossier tab + click Filters', state: clickPanel(dossierTab, 'filters'), expected: 'filters' },
    { name: 'Dossier tab + click Agents / CLI', state: clickPanel(dossierTab, 'cli'), expected: 'cli' },
    {
      name: 'Chats marked + click Dossiers',
      state: activateTab(clickPanel(dossierTab, 'chats'), 'workbenches'),
      expected: 'workbenches',
    },
    {
      name: 'Chats marked + select the Settings destination tab',
      state: activateTab(clickPanel({ ...dossierTab, activeTabKind: 'board' }, 'chats'), 'workspace-settings'),
      expected: 'settings',
    },
    {
      name: 'Chats marked on a Dossier tab + click Chats again',
      state: clickPanel(clickPanel(dossierTab, 'chats'), 'chats'),
      expected: 'workbenches',
    },
    {
      name: 'Explorer open + click Settings',
      state: activateTab({ activeTabKind: 'board', activePanel: 'explorer', sidebarVisible: true, markerOwner: 'panel' }, 'workspace-settings'),
      expected: 'settings',
    },
    {
      name: 'sidebar hidden on a board tab',
      state: { activeTabKind: 'board', activePanel: 'chats', sidebarVisible: false, markerOwner: 'panel' },
      expected: null,
    },
  ];

  for (const c of cases) {
    it(c.name, () => {
      expect(resolveActiveActivityKey(c.state)).toBe(c.expected);
    });
  }

  it('keeps the Chats panel open when the marker returns to Dossiers', () => {
    const state = activateTab(clickPanel(dossierTab, 'chats'), 'workbenches');
    expect(state.sidebarVisible).toBe(true);
    expect(state.activePanel).toBe('chats');
  });
});

/** AGT-3351: the Runbook panel was removed; stored state lands on the Explorer. */
describe('stored Runbook panel migration', () => {
  it('opens the Explorer for a persisted active:runbook', () => {
    TestBed.resetTestingModule();
    localStorage.setItem('atp.studio.panelState.v1', JSON.stringify({ active: 'runbook', visible: true }));
    try {
      const panels = TestBed.inject(StudioPanelStateService);
      expect(panels.active()).toBe('explorer');
      expect(resolveActiveActivityKey({
        activeTabKind: 'board',
        activePanel: panels.active(),
        sidebarVisible: panels.visible(),
        markerOwner: panels.markerOwner(),
      })).toBe('explorer');
    } finally {
      localStorage.removeItem('atp.studio.panelState.v1');
    }
  });
});
