import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import type { AgentWorkCall, AgentWorkDetail } from '../../../../session-events';
import { TaskService } from '../../../../../services/task.service';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { DisclosureMarkerComponent } from '../../../../../components/disclosure-marker/disclosure-marker.component';

/**
 * Drill-down for the Overview "Agent Work" block: a grouped, expandable view
 * of every tool call the agent made (the what: command, file, grep pattern -
 * behind the per-tool count chips). Self-contained: takes the job id/watchPath
 * and fetches the capped `agent-work-detail` payload when the block mounts so
 * the grouped detail is visible immediately under Agent Work.
 */
@Component({
  selector: 'app-agent-work-detail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TooltipDirective, DisclosureMarkerComponent],
  templateUrl: './agent-work-detail.component.html',
  styleUrl: './agent-work-detail.component.scss',
})
export class AgentWorkDetailComponent implements OnInit {
  readonly jobId = input.required<string>();
  readonly watchPath = input<string>();

  private readonly taskService = inject(TaskService);

  readonly open = signal(true);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly failed = signal(false);
  readonly detail = signal<AgentWorkDetail | null>(null);
  /** Tool name of the currently-expanded group, or null when all collapsed. */
  readonly expandedTool = signal<string | null>(null);

  readonly groups = computed(() => this.detail()?.groups ?? []);
  readonly hasGroups = computed(() => this.groups().length > 0);

  ngOnInit(): void {
    this.load();
  }

  toggleOpen(): void {
    const next = !this.open();
    this.open.set(next);
    if (next && !this.loaded() && !this.loading()) this.load();
  }

  toggleGroup(tool: string): void {
    this.expandedTool.update((v) => (v === tool ? null : tool));
  }

  isExpanded(tool: string): boolean {
    return this.expandedTool() === tool;
  }

  /** Short clock time for a call row, e.g. "14:03:21"; empty when unparseable. */
  callTime(ts: string | null): string {
    if (!ts) return '';
    const d = new Date(ts);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString();
  }

  /** Single-line preview of a call's argument; dash placeholder when empty. */
  argPreview(call: AgentWorkCall): string {
    const a = call.argument?.trim();
    return a ? a : '-';
  }

  callStatusLabel(call: AgentWorkCall): string {
    if (call.isError) return 'Error';
    if (!call.completed) return 'Open';
    return 'Done';
  }

  callStatusMarker(call: AgentWorkCall): string {
    if (call.isError) return '!';
    if (!call.completed) return '...';
    return 'ok';
  }

  /** HTML tooltip: full argument in a code block + the captured result line. */
  callTooltip(call: AgentWorkCall): string {
    const parts: string[] = [];
    if (call.argument?.trim()) parts.push(`<code>${escapeHtml(call.argument.trim())}</code>`);
    if (call.resultFirstLine?.trim()) {
      const tone = call.isError ? 'error' : '';
      parts.push(`<small class="${tone}">Result: ${escapeHtml(call.resultFirstLine.trim())}</small>`);
    }
    return parts.join('<br>');
  }

  private load(): void {
    this.loading.set(true);
    this.failed.set(false);
    this.taskService.getAgentWorkDetail(this.jobId(), this.watchPath()).subscribe({
      next: (d) => {
        this.detail.set(d);
        this.loaded.set(true);
        this.loading.set(false);
      },
      error: () => {
        this.failed.set(true);
        this.loading.set(false);
      },
    });
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
