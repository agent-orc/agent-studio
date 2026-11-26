import { ChangeDetectionStrategy, Component, OnInit, effect, inject, signal } from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback/pending-button.directive';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { OperatorSessionLimitsService, SESSION_LIFETIMES, lifetimeLabel, type OperatorSessionLimits } from '../../services/operator-session-limits.service';

@Component({
  selector: 'app-access-session-limits',
  standalone: true,
  imports: [PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-session-limits.component.html',
  styleUrl: './access-session-limits.component.scss',
})
export class AccessSessionLimitsComponent implements OnInit {
  readonly service = inject(OperatorSessionLimitsService);
  readonly operators = inject(OperatorAgentsService);
  readonly choices = SESSION_LIFETIMES;
  readonly label = lifetimeLabel;
  readonly draft = signal<OperatorSessionLimits>({ defaultLifetimeHours: 168, ownerLifetimeHours: null, adminLifetimeHours: 168, idleHours: 24 });
  readonly factor = signal('');
  readonly agentId = signal('');
  readonly agentLimit = signal<number | null>(null);
  readonly saved = signal(false);
  constructor() { effect(() => { const limits = this.service.limits(); if (limits) this.draft.set(limits); }); }
  ngOnInit(): void { void this.service.load(); }
  change(field: keyof OperatorSessionLimits, value: string): void {
    const hours = Number(value);
    this.draft.update(current => ({ ...current, [field]: hours === 0 && field !== 'defaultLifetimeHours' ? null : hours }));
    this.saved.set(false);
  }
  chooseAgent(id: string): void {
    this.agentId.set(id);
    this.agentLimit.set(this.operators.agents().find(agent => agent.id === id)?.lifetimeLimitHours ?? null);
  }
  async save(agent = false): Promise<void> {
    this.saved.set(await this.service.save(this.draft(), this.factor(), agent ? this.agentId() : undefined, this.agentLimit()));
    this.factor.set('');
    if (agent && this.saved()) await this.operators.load();
  }
}
