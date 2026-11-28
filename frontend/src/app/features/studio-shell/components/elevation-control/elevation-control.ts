import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { DialogComponent } from '@studio/ui/components/dialog/dialog.component';
import { StatusIndicatorComponent } from '@studio/ui/components/status-indicator/status-indicator.component';
import { NowTickService } from '../../../../services/now-tick.service';
import { AuthService } from '../../../../services/auth.service';
import { AccessElevatedSessionsComponent } from '../../../access/components/access-elevated-sessions/access-elevated-sessions.component';
import { OperatorAgentsService } from '../../../access/services/operator-agents.service';
import { armingRemaining } from '../../../access/models/operator-agent.model';
import { WorkspaceOverlaysService } from '../../../shell';

/** One global owner for live class visibility; the dialog reuses Access actions on desktop and phone. */
@Component({
  selector: 'app-elevation-control',
  standalone: true,
  imports: [DialogComponent, StatusIndicatorComponent, AccessElevatedSessionsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './elevation-control.html',
  styleUrl: './elevation-control.scss',
})
export class ElevationControlComponent {
  private readonly operators = inject(OperatorAgentsService);
  private readonly auth = inject(AuthService);
  private readonly overlays = inject(WorkspaceOverlaysService);
  readonly open = signal(false);
  private readonly now = inject(NowTickService).now;
  readonly sessions = computed(() => this.operators.agents().flatMap(agent => agent.disabledAt ? [] : agent.sessions
    .filter(session => (session.state === 'active' || session.state === 'pending') && (session.classes?.length ?? 0) > 0)
    .map(session => ({ agent: agent.name, session }))));
  readonly label = computed(() => {
    const rows = this.sessions();
    const first = rows[0];
    if (!first) return '';
    const times = first.session.elevation?.classes.filter(entry => entry.mode === 'direct-action')
      .map(entry => `${entry.id}: ${armingRemaining(entry.armedUntil, this.now())}`).join(' · ');
    return `Elevated · ${first.agent} · ends ${first.session.chosenLifetimeHours === 0 ? 'when revoked' : armingRemaining(first.session.chosenEnd ?? first.session.expiresAt, this.now())}${times ? ' · ' + times : ''}${rows.length > 1 ? ` · ${rows.length} sessions` : ''}`;
  });
  readonly armingSummary = computed(() => {
    const entry = [...(this.sessions()[0]?.session.elevation?.classes ?? [])]
      .filter(item => item.armedUntil).sort((a, b) => Date.parse(a.armedUntil!) - Date.parse(b.armedUntil!))[0];
    if (!entry) return '';
    const minutes = Math.max(0, Math.ceil((Date.parse(entry.armedUntil!) - this.now()) / 60_000));
    return `${entry.id === 'project.testing' ? 'testing' : entry.id} ${minutes === 0 ? 'lapsed' : minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h`}`;
  });
  readonly sessionEnd = computed(() => {
    const session = this.sessions()[0]?.session;
    if (!session || session.chosenLifetimeHours === 0) return '∞';
    const hours = Math.max(0, Math.ceil((Date.parse(session.chosenEnd ?? session.expiresAt) - this.now()) / 3600_000));
    return hours < 24 ? `${hours}h` : `${Math.ceil(hours / 24)}d`;
  });
  readonly lapsing = computed(() => this.sessions().flatMap(row => row.session.elevation?.classes ?? [])
    .some(entry => entry.armedUntil && Date.parse(entry.armedUntil) > this.now()
      && Date.parse(entry.armedUntil) - this.now() <= 3600_000));

  constructor() {
    effect(() => {
      if (this.auth.status()?.user?.role === 'owner') untracked(() => void this.operators.load());
    });
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible' && this.auth.status()?.user?.role === 'owner') {
        void this.operators.refresh();
      }
    }, 60_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }
  list(): void {
    this.open.set(false);
    this.operators.focusElevatedList.set(true);
    this.overlays.open('access');
  }
}
