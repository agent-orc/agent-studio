import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { StatusIndicatorComponent } from '../../../../components/status-indicator/status-indicator.component';
import { formatAccessTime } from '../../models/access.model';
import {
  formatDuration,
  killKindLabel,
  killTriggerLabel,
  propagationLabel,
  propagationTone,
  type OperatorKillDrill,
  type OperatorKillRecord,
} from '../../models/operator-agent.model';
import { OperatorAgentsService } from '../../services/operator-agents.service';

/** Recent kill switch uses listed under the status lines. */
export const RECENT_KILLS = 5;

/**
 * The Owner's kill switch (SM-2): one action that ends every operator
 * principal, session, and Operations Server principal, the last use and
 * whether its host revocation completed, and the drill that exercises the
 * switch against a test principal on a schedule. Only a pending revocation, a
 * pending host command, or a failed drill turns a line acute.
 */
@Component({
  selector: 'app-access-operator-kill-switch',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-operator-kill-switch.component.html',
  styleUrl: './access-operator-kill-switch.component.scss',
})
export class AccessOperatorKillSwitchComponent implements OnInit {
  readonly operators = inject(OperatorAgentsService);
  private readonly confirmDialog = inject(ConfirmDialogService);

  readonly reason = signal('');
  readonly status = this.operators.killSwitch;
  readonly lastKill = computed<OperatorKillRecord | null>(() =>
    this.status()?.kills.find(kill => kill.kind !== 'drill') ?? null);
  readonly lastDrill = computed<OperatorKillDrill | null>(() => this.status()?.drills[0] ?? null);
  readonly recent = computed(() => this.status()?.kills.slice(0, RECENT_KILLS) ?? []);
  readonly time = formatAccessTime;
  readonly duration = formatDuration;
  readonly kindLabel = killKindLabel;
  readonly triggerLabel = killTriggerLabel;
  readonly propagation = propagationLabel;
  readonly propagationTone = propagationTone;

  ngOnInit(): void {
    void this.operators.loadKillSwitch();
  }

  async endAll(): Promise<void> {
    if (this.operators.killing()) return;
    const confirmed = await this.confirmDialog.confirm({
      title: 'End all operator access?',
      message: 'Every operator agent is disabled, every session ends, and their Operations Server principals are revoked. '
        + 'To resume, create a new operator agent and start a session.',
      detail: this.reason().trim() || undefined,
      confirmLabel: 'End all operator access',
      kind: 'danger',
    });
    if (!confirmed) return;
    if (await this.operators.endAllAccess(this.reason().trim())) this.reason.set('');
  }

  drill(): void {
    if (!this.operators.killing()) void this.operators.runDrill();
  }

  /** The agents, sessions, standing grants, and approval requests one use ended, as one phrase. */
  scope(kill: OperatorKillRecord): string {
    const parts = kill.kind === 'session'
      ? ['1 session']
      : [count(kill.operatorAgentIds.length, 'agent'), count(kill.sessionIds.length, 'live session')];
    if (kill.standingGrantIds.length) parts.push(count(kill.standingGrantIds.length, 'standing grant'));
    if (kill.approvalIds.length) parts.push(count(kill.approvalIds.length, 'approval request'));
    return parts.join(', ');
  }

  actor(value: string): string {
    if (value.startsWith('system:')) return 'Control plane host';
    return value.startsWith('human:') ? 'Owner' : value;
  }
}

function count(value: number, noun: string): string {
  return `${value} ${noun}${value === 1 ? '' : 's'}`;
}
