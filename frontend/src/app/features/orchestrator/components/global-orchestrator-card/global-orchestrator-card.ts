import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import type { OrchestratorSession } from '../../models/orchestrator.model';
import { ConceptHelpComponent } from '../../../../components/concept-help/concept-help.component';
import { TaskService } from '../../../../services/task.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Flat status header for the singleton session that spans all projects.
 * The feed is about events, so the closed state stays to one operational
 * line; cumulative session details are available through disclosure.
 */
@Component({
  selector: 'app-global-orchestrator-card',
  standalone: true,
  imports: [ConceptHelpComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './global-orchestrator-card.html',
  styleUrl: './global-orchestrator-card.scss'
})
export class GlobalOrchestratorCardComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(TaskService);
  readonly session = signal<OrchestratorSession | null>(null);
  readonly loading = signal(true);
  readonly expanded = signal(false);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private timer: ReturnType<typeof setInterval> | null = null;
  private copyTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    this.refresh();
    // Slow poll: the global session changes only at boot or on rare resume
    // calls, so we don't need a tight tick. 60s keeps the panel honest
    // without burning HTTP traffic.
    this.timer = setInterval(() => this.refresh(), 60_000);
  }

  ngOnDestroy(): void {
    if (this.timer != null) clearInterval(this.timer);
    if (this.copyTimer != null) clearTimeout(this.copyTimer);
    this.timer = null;
    this.copyTimer = null;
  }

  toggleDetails(): void {
    this.expanded.update(expanded => !expanded);
  }

  command(session: OrchestratorSession): string {
    return `claude -r ${session.sessionId}`;
  }

  async copyCommand(event: MouseEvent, session: OrchestratorSession): Promise<void> {
    event.stopPropagation();
    const copied = await copyTextToClipboard(this.command(session));
    this.copyState.set(copied ? 'copied' : 'failed');
    if (this.copyTimer != null) clearTimeout(this.copyTimer);
    this.copyTimer = setTimeout(() => this.copyState.set('idle'), 2_000);
  }

  private refresh(): void {
    this.jobService.getGlobalOrchestratorSession().subscribe({
      next: (resp) => {
        this.session.set(resp.session ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
      }
    });
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString('en-US');
    } catch {
      return iso;
    }
  }

  formatTokens(value: number): string {
    return value.toLocaleString('en-US');
  }
}
