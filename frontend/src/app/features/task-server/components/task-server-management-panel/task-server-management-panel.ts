import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  managementActionLabel,
  type ManagementActionKind,
  type ManagementActionResult,
} from '../../models/task-server.model';

/** One management sweep offered as a button. */
interface ManagementAction {
  kind: ManagementActionKind;
  label: string;
  description: string;
  /** Danger styling for destructive sweeps. */
  danger: boolean;
}

/**
 * Management functions for the Task-Server page (AGT-1924): Archive sweep,
 * Orphan scan, and Fixture cleanup. Presentational - it takes the busy state +
 * recent results and emits a `run` event; the panel owns the service call.
 *
 * Each sweep's outcome renders quietly as a settled history row (R4): a past
 * sweep is not acute, so it uses a neutral tint, a "found 0" run reads calm,
 * and only the in-flight button carries a live "Running…" affordance.
 */
@Component({
  selector: 'app-task-server-management-panel',
  standalone: true,
  templateUrl: './task-server-management-panel.html',
  styleUrl: './task-server-management-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskServerManagementPanelComponent {
  /** Recent sweep outcomes, newest first. */
  readonly results = input<readonly ManagementActionResult[]>([]);
  /** The sweep currently running, or null when idle. */
  readonly busyAction = input<ManagementActionKind | null>(null);
  readonly run = output<{ kind: ManagementActionKind; confirmed: boolean }>();

  readonly actions: readonly ManagementAction[] = [
    { kind: 'archive-sweep', label: 'Archive sweep', description: 'Move completed tasks into the authoritative archive.', danger: false },
    { kind: 'orphan-sweep', label: 'Orphan sweep', description: 'Remove terminal folders that have no owning task.', danger: true },
    { kind: 'fixture-sweep', label: 'Fixture sweep', description: 'Remove tasks explicitly marked or identified as test fixtures.', danger: true },
    { kind: 'backup-create', label: 'Create backup', description: 'Create an encrypted, consistent backup of the installation now; writes wait while the stores are read.', danger: false },
    { kind: 'restore-verify', label: 'Verify restore', description: 'Restore the newest backup into an isolated staging host and check every file without touching live data.', danger: false },
    { kind: 'backup-retention', label: 'Apply retention', description: 'Keep the configured number of newest local backups.', danger: true },
    { kind: 'backup-upload', label: 'Copy off-host', description: 'Copy every backup that is not off this host yet to the configured target.', danger: false },
    { kind: 'maintenance-enter', label: 'Enter maintenance', description: 'Drain new work and expose the server as not ready.', danger: true },
    { kind: 'maintenance-read-only', label: 'Enter read-only', description: 'Keep reads available while refusing normal mutations.', danger: true },
    { kind: 'maintenance-exit', label: 'Exit maintenance', description: 'Return a prepared server to normal admission.', danger: false },
  ];

  actionLabel(kind: ManagementActionKind): string { return managementActionLabel(kind); }

  preview(kind: ManagementActionKind): void {
    if (this.busyAction()) return;
    this.run.emit({ kind, confirmed: false });
  }

  confirm(kind: ManagementActionKind): void {
    if (this.busyAction()) return;
    this.run.emit({ kind, confirmed: true });
  }

  hasPreview(kind: ManagementActionKind): boolean {
    return this.results().some(result => result.kind === kind && result.dryRun);
  }
}
