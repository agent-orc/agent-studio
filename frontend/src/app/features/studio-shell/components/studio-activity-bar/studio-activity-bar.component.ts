import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import type { StudioActivityItemKey } from './studio-activity-bar.active-key';

export type StudioActivityPanelKey = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook';

export interface StudioActivityBarItem {
  key: StudioActivityPanelKey;
  icon: 'folder' | 'filter' | 'cli' | 'activity' | 'runbook';
  label: string;
}

@Component({
  selector: 'app-studio-activity-bar',
  standalone: true,
  imports: [TooltipDirective, StudioIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-activity-bar.component.html',
  styleUrl: './studio-activity-bar.component.scss',
})
export class StudioActivityBarComponent {
  readonly items = input.required<readonly StudioActivityBarItem[]>();
  /**
   * The single ActivityBar item that carries the active marker, or `null`
   * when none does. Resolved by the shell from one source (see
   * `resolveActiveActivityKey`) so at most one button is ever active —
   * this is the fix for AGT-2042 (two items marked active at once).
   */
  readonly activeKey = input.required<StudioActivityItemKey | null>();
  /** Per-item badge counts. Items with count > 0 show a small badge on the icon. */
  readonly badgeCounts = input<Readonly<Record<string, number>>>({});
  readonly panelToggle = output<StudioActivityPanelKey | 'settings' | 'chats'>();
  readonly openWorkbenchesRequest = output<void>();

  badgeLabel(item: StudioActivityBarItem, count: number): string {
    if (item.key === 'activity') return `${count} fresh ${count === 1 ? 'alert' : 'alerts'}`;
    return `${count} ${count === 1 ? 'filter' : 'filters'} active`;
  }
}
