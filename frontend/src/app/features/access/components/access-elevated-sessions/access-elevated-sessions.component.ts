import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { DataGridComponent, GridCellDirective, type GridColumn } from '../../../../components/data-grid';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/pending-button.directive';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { formatAccessTime } from '../../models/access.model';
import { armingRemaining, type ElevationClass, type OperatorSession } from '../../models/operator-agent.model';
import { deviceBindingLabel } from '../../models/terminal-sign-in.model';
import { AccessService } from '../../services/access.service';
import { OperatorAgentsService } from '../../services/operator-agents.service';
import { lifetimeLabel } from '../../services/operator-session-limits.service';

interface ElevatedRow { id: string; agentId: string; agent: string; session: OperatorSession }

@Component({
  selector: 'app-access-elevated-sessions',
  standalone: true,
  imports: [DataGridComponent, GridCellDirective, PendingButtonDirective, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './access-elevated-sessions.component.html',
  styleUrl: './access-elevated-sessions.component.scss',
})
export class AccessElevatedSessionsComponent {
  readonly operators = inject(OperatorAgentsService);
  private readonly access = inject(AccessService);
  private readonly http = inject(HttpClient);
  readonly busy = signal(false);
  readonly rearming = signal<{ row: ElevatedRow; classId: string } | null>(null);
  readonly factor = signal('');
  readonly remaining = armingRemaining;
  readonly pendingAction = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly time = formatAccessTime;
  readonly lifetime = lifetimeLabel;
  readonly binding = deviceBindingLabel;
  readonly rowId = (row: ElevatedRow): string => row.id;
  readonly columns: GridColumn[] = [
    { key: 'identity', header: 'Agent and device', role: 'identity', width: '18%' },
    { key: 'lifetime', header: 'Confirmed lifetime', role: 'summary', width: '20%' },
    { key: 'activity', header: 'Activity', role: 'meta', width: '22%' },
    { key: 'classes', header: 'Classes, uses and arming', role: 'summary', width: '20%' },
    { key: 'actions', header: 'Actions', role: 'actions', width: '20%' },
  ];
  readonly rows = computed<ElevatedRow[]>(() => this.operators.agents().flatMap(agent => agent.disabledAt ? [] : agent.sessions
    .filter(session => ((session.state === 'active' || session.state === 'pending') && ((session.classes?.length ?? 0) > 0
      || Date.parse(session.expiresAt) - Date.parse(session.createdAt) > 8 * 3600_000))
      || (session.state !== 'revoked' && !!session.elevationRateEndedAt))
    .map(session => ({ id: session.id, agentId: agent.id, agent: agent.name, session }))));

  constructor() {
    const timer = setInterval(() => { if (!this.busy()) void this.operators.load(); }, 15_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
  person(id: string): string {
    return this.access.overview()?.users.find(user => user.id === id)?.displayName ?? id;
  }
  uses(entry: ElevationClass): string {
    if (entry.id !== 'project.testing') return `${entry.uses.length} / ${entry.ratePerHour} per hour`;
    const projects = [...new Set(entry.uses.map(use => use.project))];
    return projects.length ? projects.map(project => `${project}: ${entry.uses.filter(use => use.project === project).length} / ${entry.ratePerHour} per hour`).join('; ')
      : `0 / ${entry.ratePerHour} per project per hour`;
  }
  lapsing(until: string | null): boolean {
    const remaining = until ? Date.parse(until) - Date.now() : 0;
    return remaining > 0 && remaining <= 3600_000;
  }
  async rearm(): Promise<void> {
    const choice = this.rearming();
    if (!choice) return;
    if (await this.operators.rearm(choice.row.agentId, choice.row.id, choice.classId, this.factor())) {
      this.rearming.set(null); this.factor.set('');
    }
  }
  async act(row: ElevatedRow | null, action: string): Promise<void> {
    if (this.busy()) return;
    this.busy.set(true); this.pendingAction.set(row ? row.id + ':' + action : 'end-all'); this.error.set(null);
    try {
      if (!row) await firstValueFrom(this.http.post('/api/auth/iam/elevated-sessions/end-all', {}));
      else if (action === 'forget') await firstValueFrom(this.http.delete(
        `/api/auth/iam/operator-agents/${encodeURIComponent(row.agentId)}/devices/${encodeURIComponent(row.session.device!.deviceId)}`));
      else await firstValueFrom(this.http.post(
        `/api/auth/iam/operator-agents/${encodeURIComponent(row.agentId)}/sessions/${encodeURIComponent(row.id)}/actions/${action}`, {}));
      await this.operators.load();
    } catch (error: unknown) {
      this.error.set(error instanceof HttpErrorResponse && typeof error.error?.message === 'string'
        ? error.error.message : 'The session action failed. Try again.');
    } finally { this.busy.set(false); this.pendingAction.set(null); }
  }
}
