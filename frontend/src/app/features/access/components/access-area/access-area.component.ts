import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { scopeLabel, type AccessUser } from '../../models/access.model';
import { isOperatorAgentAccount } from '../../models/operator-agent.model';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { OperatorApprovalsService } from '../../services/operator-approvals.service';
import { StandingGrantsService } from '../../services/standing-grants.service';
import { AccessBindingsComponent } from '../access-bindings/access-bindings.component';
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

/**
 * The one Access area: users with their role bindings, groups, service
 * accounts (API tokens included), runner enrollments, and runners. A
 * single-user installation sees the same views with its one Owner. The
 * Owner also sees operator agents, their approval requests, standing grants,
 * kill switch, and audit trail; the server refuses those reads for everyone
 * else, which hides them.
 */
@Component({
  selector: 'app-access-area',
  standalone: true,
  imports: [
    AccessBindingsComponent, AccessGroupsComponent, AccessInviteFormComponent, AccessOperatorActivityComponent,
    AccessOperatorAgentsComponent, AccessOperatorApprovalsComponent, AccessOperatorKillSwitchComponent, AccessRunnersComponent,
    AccessServiceAccountsComponent, AccessStandingGrantsComponent, SessionManagerComponent, StatusIndicatorComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-area.component.html',
  styleUrl: './access-area.component.scss',
})
export class AccessAreaComponent implements OnInit {
  readonly access = inject(AccessService);
  readonly operators = inject(OperatorAgentsService);
  readonly standingGrants = inject(StandingGrantsService);
  readonly approvals = inject(OperatorApprovalsService);
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
    void this.access.load();
    void this.operators.load();
    void this.standingGrants.load();
    void this.approvals.load();
  }

  /** Reloads everything, including an open operator activity view. */
  reload(): void {
    void this.access.load();
    void this.operators.load();
    void this.standingGrants.load();
    void this.approvals.load();
    if (this.operators.killSwitch()) void this.operators.loadKillSwitch();
    const activity = this.operators.activityState();
    if (activity === 'ready' || activity === 'error') void this.operators.loadActivity();
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

  status(user: AccessUser): 'Disabled' | 'Invited' | 'Active' {
    if (user.disabled) return 'Disabled';
    return this.access.pendingInvitations().has(user.id) ? 'Invited' : 'Active';
  }

  async resetPassword(user: AccessUser): Promise<void> {
    if (this.access.busy()) return;
    const reset = await this.access.resetPassword(user.id);
    if (reset) this.temporaryPassword.set({ username: user.username, value: reset.temporaryPassword });
  }

  async toggleDisabled(user: AccessUser): Promise<void> {
    if (!this.access.busy()) await this.access.setUserDisabled(user.id, !user.disabled);
  }

  private roleName(id: string): string {
    return this.overview()?.roles.find(role => role.id === id)?.displayName ?? id;
  }
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`;
}
