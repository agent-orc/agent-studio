import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { DataGridComponent, GridCellDirective, type GridColumn } from '../../../../components/data-grid';
import { PendingButtonDirective } from '@studio/ui/components/async-feedback/pending-button.directive';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { formatAccessTime } from '../../models/access.model';
import type { OperatorSession } from '../../models/operator-agent.model';
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
  readonly pendingAction = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly time = formatAccessTime;
  readonly lifetime = lifetimeLabel;
  readonly binding = deviceBindingLabel;
  readonly rowId = (row: ElevatedRow): string => row.id;
  readonly columns: GridColumn[] = [
    { key: 'identity', header: 'Agent and device', role: 'identity', width: '23%' },
    { key: 'lifetime', header: 'Confirmed lifetime', role: 'summary', width: '25%' },
    { key: 'activity', header: 'Activity', role: 'meta', width: '27%' },
    { key: 'actions', header: 'Actions', role: 'actions', width: '25%' },
  ];
  readonly rows = computed<ElevatedRow[]>(() => this.operators.agents().flatMap(agent => agent.disabledAt ? [] : agent.sessions
    .filter(session => (session.state === 'active' || session.state === 'pending') && ((session.classes?.length ?? 0) > 0
      || Date.parse(session.expiresAt) - Date.parse(session.createdAt) > 8 * 3600_000))
    .map(session => ({ id: session.id, agentId: agent.id, agent: agent.name, session }))));

  constructor() {
    const timer = setInterval(() => { if (!this.busy()) void this.operators.load(); }, 15_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
  person(id: string): string {
    return this.access.overview()?.users.find(user => user.id === id)?.displayName ?? id;
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
