import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { ReviewQueueService } from '../../services/review-queue.service';
import { RemoteHostCardComponent } from '../remote-host-card/remote-host-card';
import type { HostActionKind, HostProjectSlots, RemoteHost } from '../../models/remote-host.model';
import type {
  HostProjectPolicyChange,
  RuntimeCapacityChange,
} from '../runtime-capacity-editor/runtime-capacity-editor';
import {
  boardProjectSlotsForHost,
  boardRemoteSlotsForHost,
  deriveBoardRunningTruth,
} from '../../models/running-truth';
import { AddHostWizardComponent, type ProvisionedHostDraft } from '../add-host-wizard/add-host-wizard';
import { type VisibleCliTaskCreated, type VisibleCliTaskWorkspace } from '../../../visible-cli-task';
import { RunnerSetupDialogComponent } from '../runner-setup-dialog/runner-setup-dialog';
import {
  RemoteHostTableState,
  type RemoteHostSortKey,
} from './remote-host-table-state';
import { formatDrainRate, formatReviewDuration } from './auto-review-queue-format';
import {
  groupPhysicalHosts,
  type PhysicalHostGroup,
} from '../../models/physical-host-group';
import { NotificationComponent } from '../../../../components/notification/notification.component';

/**
 * Execution Hosts settings page (AGT-1921).
 *
 * The single visible entry point into execution-host management: the local
 * machine and each remote runner in one sortable table so the whole fleet reads
 * as one picture. Each row carries current operator truth while secondary
 * identity, capability, connection, capacity, and deployment facts are disclosed
 * on demand ({@link RemoteHostCardComponent}).
 *
 * Host definitions come from {@link RemoteHostsService}; real Task Server
 * client LastSeen values hydrate liveness on every reload.
 */
@Component({
  selector: 'app-remote-hosts-panel',
  standalone: true,
  imports: [RemoteHostCardComponent, AddHostWizardComponent, RunnerSetupDialogComponent, NotificationComponent],
  templateUrl: './remote-hosts-panel.html',
  styleUrl: './remote-hosts-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostsPanelComponent implements OnInit, OnDestroy {
  private readonly service = inject(RemoteHostsService);
  private readonly tasks = inject(TaskService);
  private readonly reviewQueue = inject(ReviewQueueService);
  private readonly tableState = new RemoteHostTableState();

  readonly hosts = this.service.hosts;
  readonly loading = this.service.loading;
  readonly error = this.service.error;
  readonly identityDiagnostics = this.service.identityDiagnostics;
  readonly wizardOpen = signal(false);
  readonly showRetired = signal(false);
  readonly setupHost = signal<RemoteHost | null>(null);
  readonly pendingConfirmation = signal<{ kind: 'retire' | 'delete'; host: RemoteHost } | null>(null);
  readonly confirmationTitle = computed(() => {
    const pending = this.pendingConfirmation();
    if (!pending) return '';
    return pending.kind === 'retire' ? `Retire ${pending.host.name}?` : `Delete ${pending.host.name} permanently?`;
  });
  readonly confirmationText = computed(() => {
    const pending = this.pendingConfirmation();
    if (!pending) return '';
    if (pending.kind === 'delete') return 'This removes the already-retired identity file and cannot be undone. Historical task attribution may retain the old client id as plain text.';
    const active = pending.host.activeTaskCount ?? 0;
    const work = active > 0 ? `The ${active} running task(s) will finish first; then the client retires.` : 'With no running work, the client retires immediately.';
    return `No new leases will be granted. ${work} It remains visible and can be revived.`;
  });
  readonly workspaces = input<readonly VisibleCliTaskWorkspace[]>([]);
  readonly openTask = output<VisibleCliTaskCreated>();

  /** Ticking clock so relative heartbeat labels stay fresh without per-card timers. */
  readonly now = signal<number>(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  /** Header tallies reconcile to visible physical machines and role sub-rows. */
  readonly hostGroups = computed(() => groupPhysicalHosts(this.hosts(), this.showRetired()));
  readonly retiredCount = computed(() => this.hosts().filter(host => host.status === 'retired').length);
  readonly total = computed(() => this.hostGroups().length);
  readonly roleCount = computed(() => this.hostGroups()
    .reduce((total, group) => total + group.roles.length, 0));
  readonly onlineCount = computed(() => this.hostGroups()
    .filter(group => group.machine.status === 'online').length);
  readonly boardRunningTruth = computed(() =>
    deriveBoardRunningTruth(this.tasks.grouped().progress));
  readonly sortKey = this.tableState.sortKey;
  readonly sortDirection = this.tableState.direction;
  readonly sortedHostGroups = computed(() =>
    this.tableState.sort(this.hostGroups(), host => this.boardSlots(host)));
  readonly linkFailures = computed(() => this.hosts().filter(host =>
    host.runnerLink?.linkState === 'down'
    && host.runnerLink.readyCardsTargetHost
    && host.runnerLink.keeper?.supported));

  /** Auto-review post-processing queue snapshot (AGT-2645). */
  readonly reviewQueueSnapshot = computed(() => this.reviewQueue.snapshot());
  readonly reviewDrainRateLabel = computed(() => {
    const snapshot = this.reviewQueueSnapshot();
    return snapshot ? formatDrainRate(snapshot.drainRatePerMinute) : '-';
  });
  readonly reviewDurationLabel = computed(() => {
    const snapshot = this.reviewQueueSnapshot();
    return snapshot ? formatReviewDuration(snapshot.medianReviewDurationMs) : '-';
  });

  ngOnInit(): void {
    this.tableState.hydrate();
    this.service.ensureLoaded();
    this.reviewQueue.refresh();
    this.tickHandle = setInterval(() => this.now.set(Date.now()), 30_000);
  }

  ngOnDestroy(): void {
    if (this.tickHandle) clearInterval(this.tickHandle);
  }

  reload(): void { this.service.reload(); }
  reconnect(id: string): void { this.service.reconnect(id); }
  linkFailureMessage(host: RemoteHost): string {
    switch (host.runnerLink?.keeper?.cause) {
      case 'task-disabled': return 'The tunnel keeper Scheduled Task is disabled.';
      case 'not-running': return 'The tunnel keeper Scheduled Task is not running.';
      case 'ssh-not-running': return 'The tunnel keeper has no SSH reverse-forward process.';
      case 'probe-failing': return 'The tunnel keeper functional probe is failing.';
      default: return 'The tunnel keeper is unhealthy.';
    }
  }

  boardSlots(host: RemoteHost): number {
    const truth = this.boardRunningTruth();
    return host.role === 'local' ? truth.local : boardRemoteSlotsForHost(truth, host);
  }

  /** Per-project consumption of one host's shared slot ceiling. */
  projectSlots(host: RemoteHost): readonly HostProjectSlots[] {
    return host.role === 'local'
      ? []
      : boardProjectSlotsForHost(this.boardRunningTruth(), host);
  }

  sort(key: RemoteHostSortKey): void {
    this.tableState.selectSort(key);
  }

  ariaSort(key: RemoteHostSortKey): 'ascending' | 'descending' | 'none' {
    if (this.sortKey() !== key) return 'none';
    return this.sortDirection() === 'asc' ? 'ascending' : 'descending';
  }

  sortIndicator(key: RemoteHostSortKey): string {
    if (this.sortKey() !== key) return '';
    return this.sortDirection() === 'asc' ? '↑' : '↓';
  }

  isExpanded(hostId: string): boolean {
    return this.tableState.isExpanded(hostId);
  }

  setExpanded(hostId: string, expanded: boolean): void {
    this.tableState.setExpanded(hostId, expanded);
  }

  toggleRetired(): void { this.showRetired.update(value => !value); }

  roleSlots(group: PhysicalHostGroup): Readonly<Record<string, number>> {
    return Object.fromEntries(group.roles.map(role => [role.id, this.boardSlots(role)]));
  }

  groupSlots(group: PhysicalHostGroup): number {
    return group.roles.reduce((total, role) => total + this.boardSlots(role), 0);
  }

  openWizard(): void { this.wizardOpen.set(true); }
  closeWizard(): void { this.wizardOpen.set(false); }
  openSetup(host: RemoteHost): void { this.setupHost.set(host); }
  closeSetup(): void { this.setupHost.set(null); }

  onSetupTaskCreated(task: VisibleCliTaskCreated): void {
    this.setupHost.set(null);
    this.openTask.emit(task);
  }

  completeWizard(host: ProvisionedHostDraft): void {
    this.service.addProvisionedHost(host.name, host.address);
    this.wizardOpen.set(false);
  }

  onCapacityChange(change: RuntimeCapacityChange): void {
    this.service.setCapacity(
      change.id,
      change.maxParallelism,
      change.targetLoadPercent,
      change.rampStrategy,
    );
  }

  onProjectPolicyChange(change: HostProjectPolicyChange): void {
    this.service.setProjectPolicy(
      change.id,
      change.allowAllProjects,
      change.allowedProjectIds,
      change.expectedVersion,
    );
  }

  onAction(evt: { kind: HostActionKind; id: string }): void {
    const host = this.hosts().find(item => item.id === evt.id);
    if (!host) return;
    if (evt.kind === 'retire' || evt.kind === 'delete') {
      this.pendingConfirmation.set({ kind: evt.kind, host });
      return;
    }
    switch (evt.kind) {
      case 'reprobe': this.service.reprobe(evt.id); break;
      case 'reconnect': this.service.reconnect(evt.id); break;
      case 'drain': this.service.drain(evt.id); break;
      case 'revive': this.service.revive(evt.id); break;
    }
  }

  cancelConfirmation(): void { this.pendingConfirmation.set(null); }

  confirmLifecycleAction(): void {
    const pending = this.pendingConfirmation();
    if (!pending) return;
    this.pendingConfirmation.set(null);
    if (pending.kind === 'retire') this.service.retire(pending.host.id);
    else this.service.permanentlyDelete(pending.host.id);
  }
}
