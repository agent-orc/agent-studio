import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import {
  clientKindLabel,
  clientStateTone,
  formatRelativeTime,
  type TaskServerClient,
  type TaskServerClientKind,
  type ManagementActionKind,
  type ManagementActionResult,
} from '../../models/task-server.model';

/**
 * Client-registry list for the Task-Server page (AGT-1924). One row per
 * registered identity: emoji + name, kind badge, relative last-seen, and how
 * many tasks it owns. Presentational - the panel injects the clock so the
 * "last seen" labels tick without a per-row timer.
 *
 * Retired identities render calm (settled history, R4); no status stripe or
 * accent bar (R1). The owned-task counts are per-identity figures shown next to
 * each visible row, so they read as the row's own number, not a rolled-up total.
 */
@Component({
  selector: 'app-task-server-clients-card',
  standalone: true,
  imports: [StatusIndicatorComponent],
  templateUrl: './task-server-clients-card.html',
  styleUrl: './task-server-clients-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskServerClientsCardComponent {
  private readonly allRunnerActions: readonly { kind: ManagementActionKind; label: string }[] = [
    { kind: 'runner-drain', label: 'Drain' },
    { kind: 'runner-retire', label: 'Retire' },
    { kind: 'runner-credential-rotate', label: 'Rotate credential' },
    { kind: 'runner-revoke', label: 'Revoke' },
  ];
  readonly clients = input.required<readonly TaskServerClient[]>();
  readonly securityAvailable = input(false);
  readonly results = input<readonly ManagementActionResult[]>([]);
  readonly busyAction = input<ManagementActionKind | null>(null);
  readonly run = output<{ kind: ManagementActionKind; confirmed: boolean; runnerId?: string; runnerName?: string }>();
  readonly enrollmentName = signal('');
  /** Injected clock so relative last-seen labels stay fresh. */
  readonly now = input<number>(Date.now());

  kindLabel(kind: TaskServerClientKind): string { return clientKindLabel(kind); }
  stateTone(state: string | undefined): StatusTone { return clientStateTone(state); }
  lastSeen(client: TaskServerClient): string { return formatRelativeTime(client.lastSeenAt, this.now()); }
  retired(client: TaskServerClient): boolean { return client.kind === 'retired'; }
  manageable(client: TaskServerClient): boolean {
    return client.kind !== 'retired' && client.managementState !== 'revoked';
  }
  runnerActions(): readonly { kind: ManagementActionKind; label: string }[] {
    return this.securityAvailable() ? this.allRunnerActions : this.allRunnerActions.slice(0, 2);
  }
  hasPreview(kind: ManagementActionKind, runnerId: string): boolean {
    return this.results().some(result => result.kind === kind && result.dryRun && result.targetId === runnerId);
  }
  execute(kind: ManagementActionKind, runnerId: string, confirmed: boolean): void {
    if (!this.busyAction()) this.run.emit({ kind, runnerId, confirmed });
  }
  setEnrollmentName(event: Event): void {
    this.enrollmentName.set((event.target as HTMLInputElement).value);
  }
  hasEnrollmentPreview(): boolean {
    const name = this.enrollmentName().trim();
    return !!name && this.results().some(result =>
      result.kind === 'runner-enrollment-create' && result.dryRun && result.targetId === name);
  }
  enroll(confirmed: boolean): void {
    const runnerName = this.enrollmentName().trim();
    if (!runnerName || this.busyAction()) return;
    this.run.emit({ kind: 'runner-enrollment-create', runnerName, confirmed });
  }
}
