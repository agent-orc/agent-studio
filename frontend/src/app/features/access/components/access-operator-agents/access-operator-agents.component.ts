import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { formatAccessTime } from '../../models/access.model';
import {
  DEFAULT_OPERATOR_SESSION_HOURS,
  OPERATOR_SESSION_HOURS,
  orderedSessions,
  sessionStateLabel,
  type OneTimeOperatorSession,
  type OperatorAgent,
  type OperatorSession,
} from '../../models/operator-agent.model';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';

/**
 * Owner-only operator agents. Each agent acts only through short-lived
 * sessions the Owner starts and renews; a session token is shown once and
 * every call it makes lands in the audit trail as
 * `operator-agent:<id>/session:<sid>`, delegated by the Owner.
 */
@Component({
  selector: 'app-access-operator-agents',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [StatusIndicatorComponent],
  templateUrl: './access-operator-agents.component.html',
  styleUrl: './access-operator-agents.component.scss',
})
export class AccessOperatorAgentsComponent {
  readonly operators = inject(OperatorAgentsService);
  private readonly access = inject(AccessService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly document = inject(DOCUMENT);

  readonly name = signal('');
  readonly description = signal('');
  /** Session name and lifetime drafts per agent id. */
  readonly sessionNames = signal<Readonly<Record<string, string>>>({});
  readonly lifetimes = signal<Readonly<Record<string, number>>>({});
  readonly issued = signal<OneTimeOperatorSession | null>(null);
  readonly copied = signal(false);
  readonly hours = OPERATOR_SESSION_HOURS;
  readonly agents = computed(() => this.operators.agents());
  readonly time = formatAccessTime;
  readonly stateLabel = sessionStateLabel;

  constructor() {
    effect(() => {
      if (this.operators.state() === 'denied' || this.access.state() === 'denied') this.dismiss();
    });
  }

  sessions(agent: OperatorAgent): OperatorSession[] {
    return orderedSessions(agent.sessions);
  }

  /** A user id or username as the person's display name; unknown values stay as they are. */
  person(value: string | null | undefined): string {
    if (!value) return '';
    const user = this.access.overview()?.users.find(candidate => candidate.id === value || candidate.username === value);
    return user?.displayName ?? value;
  }

  lifetime(agentId: string): number {
    return this.lifetimes()[agentId] ?? DEFAULT_OPERATOR_SESSION_HOURS;
  }

  setLifetime(agentId: string, value: string): void {
    const hours = Number(value);
    if (this.hours.includes(hours)) this.lifetimes.update(current => ({ ...current, [agentId]: hours }));
  }

  sessionName(agentId: string): string {
    return this.sessionNames()[agentId] ?? '';
  }

  setSessionName(agentId: string, value: string): void {
    this.sessionNames.update(current => ({ ...current, [agentId]: value }));
  }

  async create(): Promise<void> {
    const name = this.name().trim();
    if (!name || this.operators.busy()) return;
    if (await this.operators.create(name, this.description().trim())) {
      this.name.set('');
      this.description.set('');
    }
  }

  async start(agent: OperatorAgent): Promise<void> {
    if (this.operators.busy()) return;
    this.copied.set(false);
    const issued = await this.operators.startSession(agent.id, this.sessionName(agent.id).trim(), this.lifetime(agent.id));
    if (!issued) return;
    this.issued.set(issued);
    this.setSessionName(agent.id, '');
  }

  async renew(agent: OperatorAgent, session: OperatorSession): Promise<void> {
    if (!this.operators.busy()) await this.operators.renewSession(agent.id, session.id, this.lifetime(agent.id));
  }

  async revoke(agent: OperatorAgent, session: OperatorSession): Promise<void> {
    if (!this.operators.busy()) await this.operators.revokeSession(agent.id, session.id);
  }

  async disable(agent: OperatorAgent): Promise<void> {
    if (this.operators.busy()) return;
    const confirmed = await this.confirmDialog.confirm({
      title: 'Disable this operator agent?',
      message: 'Every session of the agent is revoked and no new session can be started.',
      detail: agent.name,
      confirmLabel: 'Disable',
      kind: 'danger',
    });
    if (confirmed) await this.operators.disable(agent.id);
  }

  /** Narrows the activity view to one session and brings it into view. */
  showActivity(session: OperatorSession): void {
    void this.operators.filterSession(session.id);
    this.document.getElementById('access-operator-activity-title')?.scrollIntoView({ block: 'start' });
  }

  async copy(token: string): Promise<void> {
    this.copied.set(await copyTextToClipboard(token));
  }

  dismiss(): void {
    this.issued.set(null);
    this.copied.set(false);
  }
}
