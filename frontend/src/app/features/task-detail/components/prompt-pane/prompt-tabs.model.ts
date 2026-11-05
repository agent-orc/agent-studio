import type { PaneTabDef } from '../../../../components/pane-tabs/pane-tabs.component';

export type PromptPaneTabId = 'overview' | 'description' | 'timeline' | 'evidence' | 'code-review';

export function nextPromptPaneTabForJobSwitch(
  activeTab: PromptPaneTabId,
  previousJobKey: string | null,
  nextJobKey: string | null,
): PromptPaneTabId {
  return previousJobKey !== null && nextJobKey !== null && nextJobKey !== previousJobKey
    ? 'overview'
    : activeTab;
}

export function buildPromptTabs(filesCount: number, visualEvidenceCount: number): readonly PaneTabDef[] {
  return [
    {
      id: 'overview',
      label: 'Overview',
      icon: 'layout',
      testid: 'prompt-tab-overview',
    },
    {
      id: 'timeline',
      label: 'Timeline',
      icon: 'activity',
      testid: 'prompt-tab-timeline',
    },
    {
      id: 'evidence',
      label: 'Evidence',
      icon: 'check',
      testid: 'prompt-tab-evidence',
      badge: visualEvidenceCount > 0 ? visualEvidenceCount : null,
    },
    {
      id: 'code-review',
      label: 'Code Review',
      icon: 'diff',
      testid: 'prompt-tab-code-review',
    },
    {
      id: 'description',
      label: 'Docs',
      icon: 'folder',
      testid: 'prompt-tab-description',
      badge: filesCount > 1 ? filesCount : null,
    },
  ];
}

