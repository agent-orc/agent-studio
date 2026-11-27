import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnInit, afterNextRender, afterRenderEffect, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { DataGridComponent, type GridColumn } from '../../../../components/data-grid';
import { DetailLayoutComponent } from '../../../../components/detail-layout/detail-layout.component';
import { EmptyStateComponent } from '@studio/ui/components/empty-state/empty-state.component';
import { LocalOwnerSignInComponent } from '../../../../components/local-owner-sign-in/local-owner-sign-in';
import { PageOutlineComponent, type OutlineItem } from '../../../../components/page-outline/page-outline.component';
import { PageSectionComponent } from '../../../../components/page-section/page-section.component';
import { LocalOwnerSessionService } from '../../../../services/local-owner-session.service';
import { formatAccessTime } from '../../models/access.model';
import { isApiToken } from '../../models/access-view.model';
import { isOperatorAgentAccount } from '../../models/operator-agent.model';
import { AccessSelectionService, type AccessCreateKind } from '../../services/access-selection.service';
import { AccessService } from '../../services/access.service';
import { BackupActivityService } from '../../services/backup-activity.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { OperatorApprovalsService } from '../../services/operator-approvals.service';
import { StandingGrantsService } from '../../services/standing-grants.service';
import { AccessAccountDetailComponent } from '../access-account-detail/access-account-detail.component';
import { AccessAccountFormComponent } from '../access-account-form/access-account-form.component';
import { AccessBackupActivityComponent } from '../access-backup-activity/access-backup-activity.component';
import { AccessBindingFormComponent } from '../access-binding-form/access-binding-form.component';
import { AccessBindingsComponent } from '../access-bindings/access-bindings.component';
import { AccessBreakGlassComponent } from '../access-break-glass/access-break-glass';
import { AccessEnrollmentFormComponent } from '../access-enrollment-form/access-enrollment-form.component';
import { AccessGroupDetailComponent } from '../access-group-detail/access-group-detail.component';
import { AccessGroupFormComponent } from '../access-group-form/access-group-form.component';
import { AccessGroupsComponent } from '../access-groups/access-groups.component';
import { AccessInviteFormComponent } from '../access-invite-form/access-invite-form.component';
import { AccessOperatorActivityComponent } from '../access-operator-activity/access-operator-activity.component';
import { AccessElevatedSessionsComponent } from '../access-elevated-sessions/access-elevated-sessions.component';
import { AccessSessionLimitsComponent } from '../access-session-limits/access-session-limits.component';
import { AccessOperatorAgentsComponent } from '../access-operator-agents/access-operator-agents.component';
import { AccessOperatorApprovalsComponent } from '../access-operator-approvals/access-operator-approvals.component';
import { AccessOperatorKillSwitchComponent } from '../access-operator-kill-switch/access-operator-kill-switch.component';
import { AccessPeopleComponent } from '../access-people/access-people.component';
import { AccessPersonDetailComponent } from '../access-person-detail/access-person-detail.component';
import { AccessRunnerDetailComponent } from '../access-runner-detail/access-runner-detail.component';
import { AccessRunnersComponent } from '../access-runners/access-runners.component';
import { AccessServiceAccountsComponent } from '../access-service-accounts/access-service-accounts.component';
import { AccessStandingGrantsComponent } from '../access-standing-grants/access-standing-grants.component';
import { SessionManagerComponent } from '../session-manager/session-manager.component';

interface FactorEvent { sequence: number; at: string; actor: string; session?: string | null; operation: string; target?: string | null }
interface DetailHead { key: string; eyebrow: string; title: string; subtitle: string | null }

const CREATE: Record<AccessCreateKind, Omit<DetailHead, 'key'>> = {
  invite: { eyebrow: 'People', title: 'Invite a person', subtitle: 'Creates a sign-in link that works once and for 24 hours.' },
  group: { eyebrow: 'Groups', title: 'New group', subtitle: null },
  'service-account': { eyebrow: 'Service accounts', title: 'New service account', subtitle: null },
  enrollment: { eyebrow: 'Runners', title: 'New enrollment code', subtitle: 'For a runner, a run host, or the orchestration engine.' },
  binding: { eyebrow: 'Role bindings', title: 'Add a role binding', subtitle: null },
};

/** The page is wide enough for the outline as a column next to the sections. */
const WIDE_PX = 1180;

/**
 * Access principals, session limits and elevated sessions share one outline and grid grammar.
 * A single-user installation sees the same sections with its one Owner. The
 * Owner also sees operator agents, their approval requests, standing grants,
 * kill switch, audit trail, and the backup records of that trail; the server
 * refuses those reads for everyone else, which hides them.
 */
@Component({
  selector: 'app-access-area',
  standalone: true,
  imports: [
    AccessElevatedSessionsComponent, AccessSessionLimitsComponent, AccessAccountDetailComponent, AccessAccountFormComponent, AccessBackupActivityComponent, AccessBindingFormComponent, AccessBindingsComponent,
    AccessBreakGlassComponent, AccessEnrollmentFormComponent, AccessGroupDetailComponent, AccessGroupFormComponent, AccessGroupsComponent,
    AccessInviteFormComponent, AccessOperatorActivityComponent, AccessOperatorAgentsComponent, AccessOperatorApprovalsComponent,
    AccessOperatorKillSwitchComponent, AccessPeopleComponent, AccessPersonDetailComponent, AccessRunnerDetailComponent, AccessRunnersComponent,
    AccessServiceAccountsComponent, AccessStandingGrantsComponent, DataGridComponent, DetailLayoutComponent, EmptyStateComponent,
    LocalOwnerSignInComponent, PageOutlineComponent, PageSectionComponent, SessionManagerComponent,
  ],
  providers: [AccessSelectionService],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-area.component.html',
  styleUrl: './access-area.component.scss',
})
export class AccessAreaComponent implements OnInit {
  readonly access = inject(AccessService);
  readonly selection = inject(AccessSelectionService);
  private readonly http = inject(HttpClient);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly pageOutline = viewChild(PageOutlineComponent);
  readonly factorEvents = signal<readonly FactorEvent[]>([]);
  readonly localOwner = inject(LocalOwnerSessionService);
  readonly operators = inject(OperatorAgentsService);
  readonly standingGrants = inject(StandingGrantsService);
  readonly approvals = inject(OperatorApprovalsService);
  readonly backupActivity = inject(BackupActivityService);
  /** Shown once the server answered as for the Owner; a refusal or a missing route keeps it hidden. */
  readonly showBackupActivity = computed(() => ['ready', 'error'].includes(this.backupActivity.state()));
  readonly overview = this.access.overview;
  readonly wide = signal(false);

  readonly factorColumns: GridColumn[] = [
    { key: 'operation', subKey: 'target', header: 'Event', role: 'identity', width: '24%' },
    { key: 'actor', header: 'Actor', role: 'summary' },
    { key: 'session', header: 'Session', role: 'meta', width: '11rem' },
    { key: 'time', header: 'Time', role: 'meta', width: '13rem', numeric: true },
  ];
  readonly factorRows = computed(() => this.factorEvents().map(entry => ({ ...entry, time: formatAccessTime(entry.at) })));
  readonly factorId = (row: { sequence: number }) => String(row.sequence);

  /** Rows per section; every count equals the rows its grid shows. */
  readonly counts = computed(() => {
    const overview = this.overview();
    const accounts = overview?.serviceAccounts.filter(account => !isOperatorAgentAccount(account)) ?? [];
    return {
      users: overview?.users.length ?? 0,
      groups: overview?.groups.length ?? 0,
      serviceAccounts: accounts.filter(account => !isApiToken(account)).length,
      apiTokens: accounts.filter(isApiToken).length,
      runners: this.access.runners().length,
      bindings: overview?.bindings.length ?? 0,
    };
  });
  readonly singleUser = computed(() => {
    const counts = this.counts();
    return counts.users === 1 && counts.groups === 0 && counts.serviceAccounts === 0 && counts.apiTokens === 0;
  });

  readonly outline = computed<OutlineItem[]>(() => {
    const counts = this.counts();
    const operator: OutlineItem[] = this.operators.available() ? [
      { id: 'access-session-limits', label: 'Session limits', group: 'Operator agents' },
      { id: 'access-elevated-sessions', label: 'Elevated sessions' },
      { id: 'access-operators', label: 'Operator agents' },
      ...(this.approvals.state() !== 'denied' ? [{ id: 'access-approvals', label: 'Approval requests' }] : []),
      ...(this.standingGrants.state() !== 'denied' ? [{ id: 'access-grants', label: 'Standing grants' }] : []),
      { id: 'access-kill-switch', label: 'Kill switch' },
      { id: 'access-break-glass', label: 'Break-glass windows' },
      { id: 'access-operator-activity', label: 'Operator activity' },
    ] : [];
    return [
      { id: 'access-people', label: 'People', count: counts.users, group: 'Who' },
      { id: 'access-groups', label: 'Groups', count: counts.groups },
      { id: 'access-accounts', label: 'Service accounts', count: counts.serviceAccounts, group: 'Machines' },
      { id: 'access-api-tokens', label: 'API tokens', count: counts.apiTokens },
      { id: 'access-runners', label: 'Runners', count: counts.runners },
      { id: 'access-bindings', label: 'Role bindings', count: counts.bindings, group: 'By scope' },
      ...operator,
      { id: 'access-factor-activity', label: 'Factor activity', count: this.factorEvents().length, group: 'Audit' },
      ...(this.showBackupActivity() ? [{ id: 'access-backup-activity', label: 'Backup activity' }] : []),
    ];
  });

  /** Heading of the detail area for the selection; null closes it (nothing selected, or the row is gone). */
  readonly detail = computed<DetailHead | null>(() => {
    const selected = this.selection.selection();
    const overview = this.overview();
    if (!selected || !overview) return null;
    if (selected.mode === 'create') return { key: `create:${selected.kind}`, ...CREATE[selected.kind] };
    const key = `${selected.kind}:${selected.id}`;
    switch (selected.kind) {
      case 'user': {
        const user = overview.users.find(candidate => candidate.id === selected.id);
        return user ? { key, eyebrow: 'Person', title: user.displayName, subtitle: user.username } : null;
      }
      case 'group': {
        const group = overview.groups.find(candidate => candidate.id === selected.id);
        return group ? { key, eyebrow: 'Group', title: group.name, subtitle: group.description || null } : null;
      }
      case 'runner': {
        const runner = this.access.runners().find(candidate => candidate.id === selected.id);
        return runner ? { key, eyebrow: 'Runner', title: runner.name, subtitle: runner.id } : null;
      }
      case 'enrollment': {
        const enrollment = this.access.enrollments().find(candidate => candidate.id === selected.id);
        return enrollment ? { key, eyebrow: 'Enrollment code', title: enrollment.name, subtitle: null } : null;
      }
      default: {
        const account = overview.serviceAccounts.find(candidate => candidate.id === selected.id);
        return account ? { key, eyebrow: isApiToken(account) ? 'API token' : 'Service account', title: account.name, subtitle: account.description || null } : null;
      }
    }
  });

  constructor() {
    afterRenderEffect(() => {
      if (!this.operators.focusElevatedList() || !this.overview() || !this.operators.available()) return;
      const outline = this.pageOutline();
      if (!outline) return;
      outline.jump('access-elevated-sessions');
      this.operators.focusElevatedList.set(false);
    });
    // A selection whose row is gone (deleted, or access refused) closes instead of pointing at nothing.
    effect(() => {
      if (this.selection.selection() && !this.detail()) untracked(() => this.selection.close());
    });
    afterNextRender(() => {
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(entries => this.wide.set((entries[0]?.contentRect.width ?? 0) >= WIDE_PX));
      observer.observe(this.host.nativeElement);
      this.destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  ngOnInit(): void {
    void this.loadFactorEvents();
    void this.access.load();
    void this.operators.load();
    void this.standingGrants.load();
    void this.approvals.load();
    void this.backupActivity.load();
  }

  /** Reloads everything, including an open operator activity view. */
  reload(): void {
    void this.loadFactorEvents();
    void this.access.load();
    void this.operators.load();
    void this.standingGrants.load();
    void this.approvals.load();
    void this.backupActivity.load();
    if (this.operators.killSwitch()) void this.operators.loadKillSwitch();
    const activity = this.operators.activityState();
    if (activity === 'ready' || activity === 'error') void this.operators.loadActivity();
  }

  private async loadFactorEvents(): Promise<void> {
    try {
      this.factorEvents.set(await firstValueFrom(this.http.get<readonly FactorEvent[]>('/api/auth/audit/factors')));
    } catch { this.factorEvents.set([]); }
  }
}
