import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';

interface RemoteChatStatus {
  contextKey: string;
  state: 'queued' | 'running';
  runnerId: string;
  queuedAt: string;
  reason: string | null;
}

@Component({
  selector: 'app-orchestrator-chat-waiting',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-chat-waiting.component.html',
  styleUrl: './orchestrator-chat-waiting.component.scss',
})
export class OrchestratorChatWaitingComponent implements OnInit, OnDestroy {
  private readonly tasks = inject(TaskService);
  readonly project = input<string | null>(null);
  readonly contextKey = input<string | null>(null);
  readonly sending = input(false);
  readonly status = signal<RemoteChatStatus | null>(null);
  readonly waitingLabel = computed(() => {
    const status = this.status();
    if (!this.sending() || status?.state !== 'queued' || status.contextKey !== this.contextKey()) return null;
    const since = new Date(status.queuedAt).toLocaleTimeString('en-US');
    return `Waiting for ${status.runnerId} since ${since}. ${status.reason ?? 'The runner has not picked up this turn.'}`;
  });
  private timer: VisibleIntervalHandle | null = null;

  constructor() {
    effect(() => {
      if (this.sending()) this.status.set(null);
    });
  }

  ngOnInit(): void {
    this.timer = setVisibleInterval(() => this.refresh(), 2_000);
  }

  ngOnDestroy(): void {
    if (this.timer !== null) clearVisibleInterval(this.timer);
  }

  private refresh(): void {
    const project = this.project();
    const key = this.contextKey();
    if (!project || !key || !this.sending()) return;
    this.tasks.getRemoteChatWorkStatus(project, key).subscribe({
      next: status => {
        if (this.contextKey() === key && this.sending())
          this.status.set(status ? { ...status, contextKey: key } : null);
      },
      error: () => this.status.update(current => current?.contextKey === key ? null : current),
    });
  }
}
