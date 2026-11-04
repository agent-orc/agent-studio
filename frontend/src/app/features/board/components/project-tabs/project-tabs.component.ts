import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { projectIdentity } from '../../../../services/project-identity.util';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
export interface ProjectRunnerIndicator { cls: 'running' | 'paused' | 'idle'; tone: StatusTone; label: string; }

/**
 * Compact token rollup rendered next to the project name on the board's
 * project chip strip. Aggregated client-side from every `TaskInfo.tokenSummary`
 * for the project so it reflects the same per-task numbers the kanban card
 * popover shows; the orchestrator-log-based `/api/projects/.../token-summary`
 * endpoint feeds the deeper drill-down on the project page.
 */
export interface ProjectTokenChipInfo {
  totalTokens: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  allModelsPriced: boolean;
  jobsWithTokens: number;
  /** De-duplicated list of models seen across this project's jobs, most-recent first when available. */
  models: string[];
  label: string;
  tooltip: string;
}

export interface ProjectAutoInfo {
  /** off = manual / paused-without-active. on = auto-continuous. stopping = paused while a task is still running. */
  state: 'off' | 'on' | 'stopping';
  readyCount: number;
  icon: string;
  label: string;
  tooltip: string;
}

/**
 * Header chip strip showing each watched project. Clicks toggle the
 * project filter; a runner indicator shows when a project's pipeline
 * is active. Each chip is paired with an Auto-pickup toggle that
 * enables/disables continuous pickup of Ready tasks for that project.
 */
@Component({
  selector: 'app-project-tabs',
  standalone: true,
  imports: [TooltipDirective, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-tabs.component.html'
})
export class ProjectTabsComponent {
  readonly names = input<string[]>([]);
  readonly isActive = input.required<(name: string) => boolean>();
  readonly runnerIndicator = input.required<(name: string) => ProjectRunnerIndicator | null>();
  readonly autoInfo = input.required<(name: string) => ProjectAutoInfo>();
  readonly projectTokens = input<((name: string) => ProjectTokenChipInfo | null) | null>(null);

  /**
   * Project chip clicked. `additive` is true when the user held Ctrl/Cmd
   * — that signals "extend the multi-select" rather than the default
   * single-select switch.
   */
  readonly toggleRequest = output<{ name: string; additive: boolean }>();
  readonly toggleAuto = output<string>();
  readonly openShell = output<string>();

  readonly identityFor = (name: string) => projectIdentity(name);
}
