/**
 * Pure helpers that turn the studio-shell's reactive state into MenuItem
 * lists for the shared <app-menu> surfaces (tab right-click + project
 * picker). Extracted out of studio-shell.component.ts to keep the
 * component's TypeScript size under its baseline budget after F23.
 */
import type { MenuItem } from '@studio/ui/components/menu/index';

export interface ProjectPickerRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  isActive: boolean;
}

export interface TabCtxMenuInputs {
  totalTabs: number;
  hasTabsToRight: boolean;
  hasTabsToLeft: boolean;
  /** When the tab is a task, provide its identifiers for copy actions. */
  task?: { title: string; id: string; key?: string | null } | null;
}

export function buildTabCtxMenuItems(input: TabCtxMenuInputs): readonly MenuItem[] {
  const items: MenuItem[] = [];

  if (input.task) {
    items.push(
      { kind: 'row', id: 'copy-name', label: 'Copy Name' },
      { kind: 'row', id: 'copy-id', label: 'Copy ID' },
    );
    if (input.task.key) {
      items.push({ kind: 'row', id: 'copy-key', label: `Copy Key (${input.task.key})` });
    }
    items.push({ kind: 'separator' });
  }

  items.push({ kind: 'row', id: 'close', label: 'Close' });
  items.push(
    {
      kind: 'row',
      id: 'close-others',
      label: 'Close Others',
      disabled: input.totalTabs <= 1,
    },
    {
      kind: 'row',
      id: 'close-right',
      label: 'Close to the Right',
      disabled: !input.hasTabsToRight,
    },
    {
      kind: 'row',
      id: 'close-left',
      label: 'Close to the Left',
      disabled: !input.hasTabsToLeft,
    },
    { kind: 'separator' },
    { kind: 'row', id: 'close-all', label: 'Close All' },
  );
  return items;
}

export interface ProjectPickerInputs {
  rows: readonly ProjectPickerRow[];
  totalProjectJobs: number;
  allProjectsActive: boolean;
  activeTabKind: string | undefined;
}

export function buildProjectPickerItems(input: ProjectPickerInputs): readonly MenuItem[] {
  const items: MenuItem[] = [
    {
      kind: 'row',
      id: '__all__',
      label: 'All projects',
      leadingGlyph: { background: 'var(--studio-bg-hover)', initial: '◫' },
      trailingBadge: String(input.totalProjectJobs),
      active: input.allProjectsActive,
    },
  ];
  for (const p of input.rows) {
    items.push({
      kind: 'row',
      id: p.name,
      label: p.name,
      leadingGlyph: { background: p.color, initial: p.initial },
      trailingBadge: String(p.totalJobs),
      active: p.isActive && input.activeTabKind !== 'welcome',
      tooltip: `${p.name} (double-click for Deck)`,
    });
  }
  return items;
}
