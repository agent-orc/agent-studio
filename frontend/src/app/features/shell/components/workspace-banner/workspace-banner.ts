import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { NotificationService } from '@studio/ui/services/notification.service';
import type { NotificationKind } from '@studio/ui/models/app-dialog.model';
import { RemoteQueueStarvationBannerComponent } from '../remote-queue-starvation-banner/remote-queue-starvation-banner';
import { AcceptedIntegrationAlertBannerComponent } from '../accepted-integration-alert-banner/accepted-integration-alert-banner';

/**
 * F56: workspace auto-review verdicts now render as toasts in the unified
 * notification stack instead of a positioned inline banner. This component
 * still owns the polling + topic-mapping logic and pushes toasts via
 * NotificationService.
 */
interface BusDecisionMessage {
  id: string;
  createdAt: string;
  kind: string;
  topic?: string | null;
  summary?: string | null;
  jobId?: string | null;
  project?: string | null;
  tags?: string[] | null;
}

interface DisplayDecision {
  id: string;
  createdAt: number;
  topic: string;
  jobId: string;
  project: string;
  summary: string;
}

const POLL_INTERVAL_MS = 5000;
const MIN_DISPLAY_MS = 30000;

const BANNER_TOPICS: ReadonlySet<string> = new Set([
  'decision',
  'reissue',
  'escalate',
  'giveup',
  'accept',
  'accept-as-done',
]);

@Component({
  selector: 'app-workspace-banner',
  standalone: true,
  host: { 'data-testid': 'workspace-banner' },
  imports: [AcceptedIntegrationAlertBannerComponent, RemoteQueueStarvationBannerComponent],
  templateUrl: './workspace-banner.html',
  styleUrl: './workspace-banner.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WorkspaceBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly notify = inject(NotificationService);

  readonly projects = input<readonly string[]>([]);

  private readonly current = signal<DisplayDecision | null>(null);
  private readonly dismissedId = signal<string | null>(null);

  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private activeToastId: number | null = null;
  private lastToastedId: string | null = null;

  ngOnInit(): void {
    this.pollNow();
    this.pollTimer = setInterval(() => this.pollNow(), POLL_INTERVAL_MS);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private kindFor(topic: string): NotificationKind {
    switch (topic) {
      case 'accept':
      case 'accept-as-done':
      case 'decision':  return 'success';
      case 'reissue':   return 'info';
      case 'escalate':  return 'warning';
      case 'giveup':    return 'error';
      default:          return 'accent';
    }
  }

  private iconFor(topic: string): string {
    switch (topic) {
      case 'reissue':   return '↺';
      case 'escalate':  return '⚠';
      case 'decision':  return '✓';
      case 'giveup':    return '✕';
      case 'heuristic': return '~';
      default:          return '🤖';
    }
  }

  private headlineFor(msg: DisplayDecision): string {
    if (msg.summary && msg.summary.trim().length > 0) return msg.summary;
    const target = msg.jobId || '(unknown task)';
    switch (msg.topic) {
      case 'reissue':        return `Auto-review sent "${target}" back to ready for another attempt.`;
      case 'escalate':       return `Auto-review escalated "${target}" for human attention.`;
      case 'decision':
      case 'accept':
      case 'accept-as-done': return `Auto-review accepted "${target}" as done. Waiting for your approval in human review.`;
      case 'giveup':         return `Auto-review gave up on "${target}".`;
      default:               return `Auto-review verdict for "${target}".`;
    }
  }

  private pushToast(msg: DisplayDecision): void {
    if (this.lastToastedId === msg.id) return;
    if (this.activeToastId !== null) {
      this.notify.dismiss(this.activeToastId);
    }
    this.lastToastedId = msg.id;

    const kind = this.kindFor(msg.topic);
    const autoKinds = new Set<NotificationKind>(['success', 'info']);
    const durationMs = autoKinds.has(kind) ? 6000 : 0;

    this.activeToastId = this.notify.notify({
      kind,
      title: 'Auto-review verdict',
      message: this.headlineFor(msg),
      source: `in ${msg.project}`,
      durationMs,
    });
  }

  private pollNow(): void {
    const projects = this.projects();
    if (!projects.length) return;
    let bestSoFar: DisplayDecision | null = this.current();
    let pending = projects.length;
    for (const project of projects) {
      const url = `/api/bus/${encodeURIComponent(project)}/messages?kind=decision&tag=orchestrator-chat&limit=5`;
      this.http.get<BusDecisionMessage[]>(url).subscribe({
        next: items => {
          for (const m of items ?? []) {
            const created = Date.parse(m.createdAt);
            if (isNaN(created)) continue;
            if (Date.now() - created > MIN_DISPLAY_MS) continue;
            const topic = (m.topic ?? '').toLowerCase() || 'decision';
            if (!BANNER_TOPICS.has(topic)) continue;
            if (!bestSoFar || created > bestSoFar.createdAt) {
              bestSoFar = {
                id: m.id,
                createdAt: created,
                topic,
                jobId: m.jobId ?? '(unknown job)',
                project: m.project ?? project,
                summary: m.summary ?? ''
              };
            }
          }
          if (--pending === 0 && bestSoFar) {
            const cur = this.current();
            if (!cur || bestSoFar.createdAt > cur.createdAt) {
              this.current.set(bestSoFar);
              this.pushToast(bestSoFar);
            }
          }
        },
        error: () => { pending--; }
      });
    }
  }
}
