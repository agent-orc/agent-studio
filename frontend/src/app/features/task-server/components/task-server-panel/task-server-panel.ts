import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TaskServerService } from '../../services/task-server.service';
import { TaskServerClientsCardComponent } from '../task-server-clients-card/task-server-clients-card';
import { TaskServerManagementPanelComponent } from '../task-server-management-panel/task-server-management-panel';
import { ArchiveRetentionCardComponent } from '../archive-retention-card/archive-retention-card';
import {
  evidenceStateLabel,
  evidenceStateTone,
  formatBytes,
  formatRelativeTime,
  healthLabel,
  healthTone,
  phaseLabel,
  type ManagementActionKind,
} from '../../models/task-server.model';

/**
 * Task-Server settings page (AGT-1924).
 *
 * The operator's read-context for the durable task server the platform talks
 * to: the connected local or networked URL, the
 * workspace store it owns (root, size, task/project/identity counts), the
 * durable evidence inventory, the registered Runner service identities,
 * and the management sweeps (archive / orphan / fixture). See
 * docs/research/remote-ready-kickoff-2026-07.md.
 *
 * Data comes from the authenticated `GET /api/v1/management/status` contract
 * shared with the Task Server recovery console. Status is
 * encoded with dots + badges + tint, never a left accent bar (R1); acute tone
 * is reserved for a genuinely unreachable server (R4); every colour reads a
 * --studio-* token so both themes track (R5).
 */
@Component({
  selector: 'app-task-server-panel',
  standalone: true,
  imports: [TaskServerClientsCardComponent, TaskServerManagementPanelComponent, ArchiveRetentionCardComponent, TooltipDirective],
  templateUrl: './task-server-panel.html',
  styleUrl: './task-server-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskServerPanelComponent implements OnInit, OnDestroy {
  private readonly service = inject(TaskServerService);

  readonly status = this.service.status;
  readonly loading = this.service.loading;
  readonly error = this.service.error;
  readonly unavailable = this.service.unavailable;
  readonly busyAction = this.service.busyAction;
  readonly results = this.service.recentResults;

  /** Ticking clock so relative last-seen / commit-age labels stay fresh. */
  readonly now = signal<number>(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  readonly connection = computed(() => this.status()?.connection ?? null);
  readonly store = computed(() => this.status()?.store ?? null);
  readonly evidence = computed(() => this.status()?.evidence ?? null);
  readonly clients = computed(() => this.status()?.clients ?? []);

  /** Relative age of the latest durable evidence write. */
  readonly lastEvidenceLabel = computed(() =>
    formatRelativeTime(this.evidence()?.lastWriteAt, this.now()));

  // Pure display helpers exposed to the template.
  readonly formatBytes = formatBytes;
  readonly phaseLabel = phaseLabel;
  readonly healthLabel = healthLabel;
  readonly healthTone = healthTone;
  readonly evidenceStateLabel = evidenceStateLabel;
  readonly evidenceStateTone = evidenceStateTone;

  ngOnInit(): void {
    this.service.ensureLoaded();
    void this.service.loadArchiveTarget();
    this.tickHandle = setInterval(() => this.now.set(Date.now()), 30_000);
  }

  ngOnDestroy(): void {
    if (this.tickHandle) clearInterval(this.tickHandle);
  }

  reload(): void { void this.service.reload(); }

  requestSignIn(): void { this.service.requestSignIn(); }

  onRun(event: { kind: ManagementActionKind; confirmed: boolean }): void {
    void this.service.runAction(event.kind, event.confirmed);
  }

  onRunnerRun(event: { kind: ManagementActionKind; confirmed: boolean; runnerId?: string; runnerName?: string }): void {
    void this.service.runAction(event.kind, event.confirmed, event.runnerId, event.runnerName);
  }
}
