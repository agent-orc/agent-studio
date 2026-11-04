import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { formatAccessTime } from '../../models/access.model';
import {
  GRANT_ENVIRONMENTS,
  GRANT_SCOPE_KINDS,
  grantStateLabel,
  isTimeOfDay,
  orderedGrants,
  scopeKindCovers,
  scopeLabel,
  tierLabel,
  windowLabel,
  type GrantScopeKind,
  type OperatorOperation,
  type OperatorTier,
  type StandingGrant,
  type StandingGrantScope,
} from '../../models/standing-grant.model';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { StandingGrantsService, type GrantStepUp } from '../../services/standing-grants.service';

const TIERS: readonly OperatorTier[] = ['T0', 'T1', 'T2'];

/**
 * Owner-only standing grants (SM-4): the recorded T1 authority of one
 * operator agent over an operation set, a scope, an hourly rate, and an
 * optional daily window. Issuing or renewing asks for a fresh second factor;
 * revoking does not. The operation catalogue is listed by tier above.
 */
@Component({
  selector: 'app-access-standing-grants',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-standing-grants.component.html',
  styleUrl: './access-standing-grants.component.scss',
})
export class AccessStandingGrantsComponent {
  readonly grantsService = inject(StandingGrantsService);
  private readonly operators = inject(OperatorAgentsService);
  private readonly access = inject(AccessService);
  private readonly confirmDialog = inject(ConfirmDialogService);

  readonly scopeKinds = GRANT_SCOPE_KINDS;
  readonly environments = GRANT_ENVIRONMENTS;
  readonly time = formatAccessTime;
  readonly stateLabel = grantStateLabel;
  readonly scopeText = scopeLabel;
  readonly windowText = windowLabel;
  readonly tierText = tierLabel;

  readonly principal = signal('');
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly scopeKind = signal<GrantScopeKind>('project');
  readonly project = signal('');
  readonly environment = signal('stage');
  readonly host = signal('');
  readonly rate = signal<number | null>(null);
  readonly windowStart = signal('');
  readonly windowEnd = signal('');
  readonly days = signal<number | null>(null);
  readonly reason = signal('');
  readonly code = signal('');

  readonly catalogue = computed(() => this.grantsService.catalogue());
  readonly tiers = computed(() => {
    const operations = this.catalogue()?.operations ?? [];
    return TIERS.map(tier => ({ tier, operations: operations.filter(operation => operation.tier === tier) }));
  });
  readonly routine = computed(() => (this.catalogue()?.operations ?? []).filter(operation => operation.tier === 'T1'));
  readonly agents = computed(() => this.operators.agents().filter(agent => !agent.disabledAt));
  readonly grants = computed(() => orderedGrants(this.grantsService.grants()));
  readonly dayOptions = computed(() => Array.from({ length: this.catalogue()?.maxGrantDays ?? 30 }, (_, index) => index + 1));
  readonly lifetime = computed(() => this.days() ?? this.catalogue()?.defaultGrantDays ?? 7);
  readonly ratePerHour = computed(() => this.rate() ?? this.catalogue()?.defaultRatePerHour ?? 10);
  readonly principalId = computed(() => this.principal() || this.agents()[0]?.id || '');
  readonly windowValid = computed(() => {
    const start = this.windowStart();
    const end = this.windowEnd();
    return (!start && !end) || (isTimeOfDay(start) && isTimeOfDay(end) && start !== end);
  });
  readonly scope = computed<StandingGrantScope | null>(() => {
    const kind = this.scopeKind();
    const project = this.project().trim();
    const host = this.host().trim();
    if (kind === 'workspace') return { kind };
    if (kind === 'host') return host ? { kind, host } : null;
    if (!project) return null;
    return kind === 'project' ? { kind, project } : { kind, project, environment: this.environment() };
  });
  /** Selected operations this scope kind cannot cover; the server refuses them too. */
  readonly uncovered = computed(() => this.routine()
    .filter(operation => this.selected().has(operation.id) && !scopeKindCovers(this.scopeKind(), operation.scopeKind)));
  readonly uncoveredTitles = computed(() => this.uncovered().map(operation => operation.title).join(', '));
  readonly canIssue = computed(() => !!this.principalId() && this.selected().size > 0 && !!this.scope()
    && this.uncovered().length === 0 && this.windowValid() && !!this.reason().trim()
    && this.ratePerHour() >= 1 && this.ratePerHour() <= (this.catalogue()?.maxRatePerHour ?? 120));

  /** A user id or an operator agent id as its display name; unknown values stay as they are. */
  person(value: string | null | undefined): string {
    if (!value) return '';
    const user = this.access.overview()?.users.find(candidate => candidate.id === value || candidate.username === value);
    return user?.displayName ?? value;
  }

  operationTitle(id: string): string {
    return this.catalogue()?.operations.find(operation => operation.id === id)?.title ?? id;
  }

  issuedNote(grant: StandingGrant): string {
    const issued = `Issued ${this.time(grant.issuedAt)} by ${this.person(grant.issuedBy)}`;
    return grant.renewedAt ? `${issued}, renewed ${this.time(grant.renewedAt)}` : issued;
  }

  operationTitles(grant: StandingGrant): string {
    return grant.operations.map(id => this.operationTitle(id)).join(', ');
  }

  scopeHint(operation: OperatorOperation): string {
    return operation.scopeKind === 'workspace' ? '' : operation.scopeKind;
  }

  toggle(operationId: string, checked: boolean): void {
    this.selected.update(current => {
      const next = new Set(current);
      if (checked) next.add(operationId); else next.delete(operationId);
      return next;
    });
  }

  setScopeKind(value: string): void {
    if ((GRANT_SCOPE_KINDS as readonly string[]).includes(value)) this.scopeKind.set(value as GrantScopeKind);
  }

  setRate(value: string): void {
    const rate = Number(value);
    this.rate.set(Number.isInteger(rate) ? rate : null);
  }

  setDays(value: string): void {
    const days = Number(value);
    if (this.dayOptions().includes(days)) this.days.set(days);
  }

  async issue(): Promise<void> {
    const scope = this.scope();
    if (!scope || !this.canIssue() || this.grantsService.busy()) return;
    const start = this.windowStart();
    const issued = await this.grantsService.issue({
      principalId: this.principalId(),
      operations: [...this.selected()],
      scope,
      ratePerHour: this.ratePerHour(),
      window: start ? { startUtc: start, endUtc: this.windowEnd() } : null,
      expiresInDays: this.lifetime(),
      reason: this.reason().trim(),
    }, this.stepUp());
    this.code.set('');
    if (!issued) return;
    this.selected.set(new Set());
    this.reason.set('');
  }

  async renew(grant: StandingGrant): Promise<void> {
    if (this.grantsService.busy()) return;
    await this.grantsService.renew(grant.id, this.lifetime(), this.stepUp());
    this.code.set('');
  }

  async revoke(grant: StandingGrant): Promise<void> {
    if (this.grantsService.busy()) return;
    const confirmed = await this.confirmDialog.confirm({
      title: 'Revoke this standing grant?',
      message: 'The operator agent loses this authority with its next request.',
      detail: `${grant.principalName ?? grant.principalId}: ${this.operationTitles(grant)}`,
      confirmLabel: 'Revoke',
      kind: 'danger',
    });
    if (confirmed) await this.grantsService.revoke(grant.id, '');
  }

  /** An entered authenticator or recovery code, otherwise a passkey. */
  private stepUp(): GrantStepUp {
    const code = this.code().trim();
    return code ? { code } : { passkey: true };
  }
}
