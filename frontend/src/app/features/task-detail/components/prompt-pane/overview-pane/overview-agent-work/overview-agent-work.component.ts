import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../../../models/task.model';
import { AgentWorkSummaryPollService } from '../../../../../polling/services/agent-work-summary-poll.service';
import { AgentWorkDetailComponent } from '../../agent-work-detail/agent-work-detail.component';
import { formatAbsoluteTime, formatRelativeTime } from '../overview-pane-formatters';

/** How many tool counts the compact chip strip shows before the tooltip takes over. */
const TOP_TOOL_CHIP_COUNT = 6;

/**
 * "What did the agent actually do" block, derived from
 * `logs/session-events.jsonl` + `logs/tool-calls.jsonl`. It replaced the raw
 * SESSION row and was split out of `overview-pane.component.ts` in AGT-2819.
 *
 * The pane still decides whether to render it (`hasAgentWork()`), because that
 * same signal gates the surrounding layout.
 */
@Component({
  selector: 'app-overview-agent-work',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, AgentWorkDetailComponent],
  templateUrl: './overview-agent-work.component.html',
  styleUrl: './overview-agent-work.component.scss',
})
export class OverviewAgentWorkComponent {
  readonly formatAbsoluteTime = formatAbsoluteTime;
  readonly formatRelativeTime = formatRelativeTime;

  readonly job = input.required<TaskInfo>();

  private readonly agentWorkPoll = inject(AgentWorkSummaryPollService);

  readonly agentWork = this.agentWorkPoll.summary;

  /** Top N tool counts to render as compact chips. */
  readonly topToolCounts = computed(() => {
    const s = this.agentWork();
    if (s == null) return [];
    return s.toolCounts.slice(0, TOP_TOOL_CHIP_COUNT);
  });

  /** Comma-separated tool tooltip (full list) for the "Tools" row. */
  readonly toolCountsTooltip = computed(() => {
    const s = this.agentWork();
    if (s == null || s.toolCounts.length === 0) return '';
    return s.toolCounts.map(tc => `${tc.tool}: ${tc.count}`).join('\n');
  });

  /** Short rendering of the session id for the optional debug tooltip. */
  readonly sessionDebugTooltip = computed(() => {
    const id = this.job().sessionName;
    if (!id) return '';
    return `Session id (debug): ${id}`;
  });
}
