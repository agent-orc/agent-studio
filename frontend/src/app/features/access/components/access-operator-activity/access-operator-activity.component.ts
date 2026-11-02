import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { formatAccessTime } from '../../models/access.model';
import {
  formatAuditTime,
  orderedSessions,
  sessionStateLabel,
  shortDigest,
  type AuditForwarding,
  type AuditIntegrity,
  type OperatorAgent,
  type OperatorSession,
} from '../../models/operator-agent.model';
import { OperatorAgentsService } from '../../services/operator-agents.service';

/**
 * What operator agents did: their sessions per agent, the audit entries they
 * wrote (narrowed to one session on request), and the state of the hash chain
 * and its off-host forwarding. Only an alert from the server turns acute.
 */
@Component({
  selector: 'app-access-operator-activity',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-operator-activity.component.html',
  styleUrl: './access-operator-activity.component.scss',
})
export class AccessOperatorActivityComponent implements OnInit {
  readonly operators = inject(OperatorAgentsService);
  readonly activity = this.operators.activity;
  readonly selected = this.operators.sessionFilter;
  readonly agents = computed(() => this.activity()?.operatorAgents ?? []);
  readonly selectedSession = computed(() => {
    const id = this.selected();
    if (!id) return null;
    for (const agent of this.agents()) {
      const session = agent.sessions.find(candidate => candidate.id === id);
      if (session) return session;
    }
    return null;
  });
  readonly time = formatAuditTime;
  readonly shortTime = formatAccessTime;
  readonly digest = shortDigest;
  readonly stateLabel = sessionStateLabel;

  ngOnInit(): void {
    void this.operators.loadActivity();
  }

  sessions(agent: OperatorAgent): OperatorSession[] {
    return orderedSessions(agent.sessions);
  }

  select(sessionId: string | null): void {
    if (sessionId !== this.selected()) void this.operators.filterSession(sessionId);
  }

  verify(): void {
    if (!this.operators.verifying()) void this.operators.verify();
  }

  integrityLabel(integrity: AuditIntegrity): string {
    if (integrity.state === 'verified') return `Chain verified through entry ${integrity.verifiedThrough}`;
    if (integrity.state === 'failed') return `Chain check failed after entry ${integrity.verifiedThrough}`;
    return 'Chain not verified yet';
  }

  forwardingLabel(forwarding: AuditForwarding): string {
    if (!forwarding.configured) return 'No off-host sink configured';
    return `Forwarded ${forwarding.forwardedSequence} of ${forwarding.headSequence} to ${forwarding.sinkKind}`;
  }

  /** Dot tone: acute only on a server alert; a verified or fully forwarded chain is calm. */
  tone(alert: boolean, healthy: boolean): 'alert' | 'ok' | 'neutral' {
    return alert ? 'alert' : healthy ? 'ok' : 'neutral';
  }
}
