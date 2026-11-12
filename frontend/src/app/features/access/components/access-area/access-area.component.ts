import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { scopeLabel, type AccessUser } from '../../models/access.model';
import { isOperatorAgentAccount } from '../../models/operator-agent.model';
import { AccessService } from '../../services/access.service';
import { BackupActivityService } from '../../services/backup-activity.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { OperatorApprovalsService } from '../../services/operator-approvals.service';
import { StandingGrantsService } from '../../services/standing-grants.service';
import { AccessBackupActivityComponent } from '../access-backup-activity/access-backup-activity.component';
import { AccessBindingsComponent } from '../access-bindings/access-bindings.component';
import { AccessBreakGlassComponent } from '../access-break-glass/access-break-glass';
import { AccessGroupsComponent } from '../access-groups/access-groups.component';
import { AccessInviteFormComponent } from '../access-invite-form/access-invite-form.component';
import { AccessOperatorActivityComponent } from '../access-operator-activity/access-operator-activity.component';
import { AccessOperatorAgentsComponent } from '../access-operator-agents/access-operator-agents.component';
import { AccessOperatorApprovalsComponent } from '../access-operator-approvals/access-operator-approvals.component';
import { AccessOperatorKillSwitchComponent } from '../access-operator-kill-switch/access-operator-kill-switch.component';
import { AccessRunnersComponent } from '../access-runners/access-runners.component';
import { AccessServiceAccountsComponent } from '../access-service-accounts/access-service-accounts.component';
import { AccessStandingGrantsComponent } from '../access-standing-grants/access-standing-grants.component';
import { SessionManagerComponent } from '../session-manager/session-manager.component';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';

/**
 * The one Access area: users with their role bindings, groups, service
 * accounts (API tokens included), runner enrollments, and runners. A
 * single-user installation sees the same views with its one Owner. The
 * Owner also sees operator agents, their approval requests, standing grants,
 * kill switch, audit trail, and the backup records of that trail; the server
 * refuses those reads for everyone else, which hides them.
 */
@Component({
  selector: 'app-access-area',
  standalone: true,
  imports: [
    AccessBackupActivityComponent, AccessBindingsComponent, AccessBreakGlassComponent, AccessGroupsComponent, AccessInviteFormComponent, AccessOperatorActivityComponent,
    AccessOperatorAgentsComponent, AccessOperatorApprovalsComponent, AccessOperatorKillSwitchComponent, AccessRunnersComponent,
    AccessServiceAccountsComponent, AccessStandingGrantsComponent, SessionManagerComponent, StatusIndicatorComponent, DatePipe,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-area.component.html',
  styleUrl: './access-area.component.scss',
})
export class AccessAreaComponent implements OnInit {
  readonly access = inject(AccessService);
  private readonly http = inject(HttpClient);
  readonly factorEvents = signal<readonly { sequence: number; at: string; actor: string; session?: string | null; operation: string; target?: string | null }[]>([]);
  private readonly confirmDialog = inject(ConfirmDialogService);
  readonly operators = inject(OperatorAgentsService);
  readonly standingGrants = inject(StandingGrantsService);
  readonly approvals = inject(OperatorApprovalsService);
  readonly backupActivity = inject(BackupActivityService);
  /** Shown once the server answered as for the Owner; a refusal or a missing route keeps it hidden. */
  readonly showBackupActivity = computed(() => ['ready', 'error'].includes(this.backupActivity.state()));
  readonly overview = this.access.overview;
  readonly temporaryPassword = signal<{ username: string; value: string } | null>(null);

  constructor() {
    effect(() => {
      if (this.access.state() === 'denied') this.temporaryPassword.set(null);
    });
  }

  readonly counts = computed(() => {
    const overview = this.overview();
    return {
      users: overview?.users.length ?? 0,
      groups: overview?.groups.length ?? 0,
      serviceAccounts: overview?.serviceAccounts.filter(account => !account.disabledAt && !isOperatorAgentAccount(account)).length ?? 0,
      bindings: overview?.bindings.length ?? 0,
    };
  });
  readonly summary = computed(() => {
    const counts = this.counts();
    return [
      plural(counts.users, 'user'), plural(counts.groups, 'group'),
      plural(counts.serviceAccounts, 'service account'), plural(counts.bindings, 'role binding'),
    ];
  });
  readonly singleUser = computed(() => {
    const counts = this.counts();
    return counts.users === 1 && counts.groups === 0 && counts.serviceAccounts === 0;
  });

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
      this.factorEvents.set(await firstValueFrom(this.http.get<readonly { sequence: number; at: string; actor: string; session?: string | null; operation: string; target?: string | null }[]>('/api/auth/audit/factors')));
    } catch { this.factorEvents.set([]); }
  }

  /** Each scope the user holds a role in, directly or through a group. */
  grants(user: AccessUser): string[] {
    const overview = this.overview();
    if (!overview) return [];
    const groups = new Set(overview.groups.filter(group => group.members.includes(user.id)).map(group => group.id));
    return overview.bindings
      .filter(binding => (binding.principalKind === 'user' && binding.principalId === user.id)
        || (binding.principalKind === 'group' && groups.has(binding.principalId)))
      .map(binding => `${this.roleName(binding.role)} · ${scopeLabel(binding.scope, this.access.projects())}`
        + (binding.principalKind === 'group' ? ' (group)' : ''));
  }

  /** Account state on the shared indicator: Active succeeds, Invited waits, Disabled is settled. */
  statusTone(user: AccessUser): StatusTone {
    const status = this.status(user);
    return status === 'Active' ? 'success' : status === 'Invited' ? 'info' : 'neutral';
  }

  status(user: AccessUser): 'Disabled' | 'Invited' | 'Active' | 'No access' {
    if (user.disabled) return 'Disabled';
    if (this.access.pendingInvitations().has(user.id)) return 'Invited';
    return this.grants(user).length ? 'Active' : 'No access';
  }

  async resetPassword(user: AccessUser): Promise<void> {
    if (this.access.busy()) return;
    const reset = await this.access.resetPassword(user.id);
    if (reset) this.temporaryPassword.set({ username: user.username, value: reset.temporaryPassword });
  }

  async toggleDisabled(user: AccessUser): Promise<void> {
    if (this.access.busy()) return;
    if (!user.disabled) {
      const confirmed = await this.confirmDialog.confirm({
        eyebrow: 'Confirm disable',
        title: 'Disable this user?',
        message: 'Open sessions end immediately. The user cannot sign in until enabled again.',
        detail: user.displayName,
        confirmLabel: 'Disable',
        kind: 'danger',
      });
      if (!confirmed) return;
    }
    await this.access.setUserDisabled(user.id, !user.disabled);
  }

  private roleName(id: string): string {
    return this.overview()?.roles.find(role => role.id === id)?.displayName ?? id;
  }
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}
