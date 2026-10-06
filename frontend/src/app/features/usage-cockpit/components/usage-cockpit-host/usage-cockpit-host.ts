import { ChangeDetectionStrategy, Component, HostListener, OnDestroy, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Subscription } from 'rxjs';

import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { UsageCockpitResponse } from '../../models/usage-cockpit.model';
import { UsageAlarmStateService } from '../../state/usage-alarm-state.service';
import { UsageAlarmStatusComponent } from '../usage-alarm-status/usage-alarm-status';
import { UsageCliChipComponent } from '../usage-cli-chip/usage-cli-chip';
import { UsageCostChipComponent } from '../usage-cost-chip/usage-cost-chip';

/** Production read host for the header usage alarms. It changes visibility only. */
@Component({
  selector: 'app-usage-cockpit-host',
  standalone: true,
  imports: [UsageAlarmStatusComponent, UsageCliChipComponent, UsageCostChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './usage-cockpit-host.html',
  styleUrl: './usage-cockpit-host.scss',
})
export class UsageCockpitHostComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  readonly alarms = inject(UsageAlarmStateService);
  readonly workspaceName = input<string | null>(null);
  readonly workspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  /** Null selects the backend's default workspace. */
  readonly workspaceId = computed(() => this.workspaces().find(ws => ws.displayName === this.workspaceName())?.id ?? null);
  readonly openUsage = output<void>();
  readonly snapshot = signal<UsageCockpitResponse | null>(null);
  readonly readFailed = signal(false);
  readonly phone = signal(typeof window !== 'undefined' && window.innerWidth <= 600);
  readonly secondaryVisible = signal(typeof window !== 'undefined' && window.innerWidth > 1024);
  readonly primaryCliId = computed(() => this.snapshot()?.clis.find(cli => cli.primary)?.cliId
    ?? this.snapshot()?.clis[0]?.cliId ?? 'codex');
  readonly primaryCli = computed(() => this.snapshot()?.clis.find(cli => cli.cliId === this.primaryCliId()) ?? null);
  readonly secondaryClis = computed(() => this.snapshot()?.clis.filter(cli => cli.cliId !== this.primaryCliId()) ?? []);
  readonly primaryAlarms = computed(() => {
    this.alarms.state();
    return this.alarms.alarmsFor(`cli:${this.primaryCliId()}`);
  });
  readonly costAlarms = computed(() => {
    this.alarms.state();
    return this.alarms.alarmsFor('cost');
  });
  readonly hiddenAlarms = computed(() => {
    this.alarms.state();
    const visible = this.secondaryVisible() ? [this.primaryCliId(), ...this.secondaryClis().map(cli => cli.cliId)] : [this.primaryCliId()];
    return this.alarms.hiddenAlarms(visible);
  });

  private poll: VisibleIntervalHandle | null = null;
  private request: Subscription | null = null;
  private requestSequence = 0;
  private currentWorkspace: string | null = null;

  constructor() {
    effect(() => {
      this.workspaceId();
      untracked(() => this.refresh());
    });
  }

  @HostListener('window:resize')
  onResize(): void {
    this.phone.set(window.innerWidth <= 600);
    this.secondaryVisible.set(window.innerWidth > 1024);
  }

  ngOnInit(): void {
    this.poll = setVisibleInterval(() => this.refresh(), 30_000);
  }

  ngOnDestroy(): void {
    if (this.poll) clearVisibleInterval(this.poll);
    this.request?.unsubscribe();
  }

  /** Called when the shell changes workspace, and by the visible poll. */
  refresh(): void {
    const workspace = this.workspaceId();
    if (workspace !== this.currentWorkspace) {
      this.currentWorkspace = workspace;
      this.snapshot.set(null);
      this.readFailed.set(false);
      this.alarms.reset();
    }
    const sequence = ++this.requestSequence;
    this.request?.unsubscribe();
    const preferredCli = typeof localStorage === 'undefined' ? null : localStorage.getItem('defaultCliType');
    let params = new HttpParams();
    if (workspace) params = params.set('workspaceId', workspace);
    if (preferredCli) params = params.set('primaryCli', preferredCli);
    this.request = this.http.get<UsageCockpitResponse>('/api/usage/cockpit', { params }).subscribe({
      next: snapshot => {
        if (sequence !== this.requestSequence || workspace !== this.workspaceId()) return;
        this.snapshot.set(snapshot);
        this.readFailed.set(false);
        this.alarms.ingest(snapshot);
      },
      error: () => {
        if (sequence !== this.requestSequence || workspace !== this.workspaceId()) return;
        // Keep any confirmed alarms and last-known values, but never present a
        // failed first read as endless loading or a failed refresh as current.
        this.readFailed.set(true);
      },
    });
  }

  secondaryAlarms(cliId: string) {
    this.alarms.state();
    return this.alarms.alarmsFor(`cli:${cliId}`);
  }
}
