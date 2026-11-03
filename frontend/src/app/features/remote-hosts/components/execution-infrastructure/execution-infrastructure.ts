import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

interface ExecutionInfrastructure {
  gate: { host: string | null; transport?: 'pull' | 'ssh'; busy: number; total: number; reachability: string;
    lastReachedAt: string | null; lastFailureAt: string | null;
    workers?: readonly { id: string; name: string; slots: number; busy: number;
      lastHeartbeat: string; currentRun: string | null }[] };
  taskServer: string;
  engine: { state: string; required: boolean; lastContact: string | null; lastEngineId: string | null };
  workstation: string;
}

/** Runtime infrastructure outside the runner client registry. */
@Component({
  selector: 'app-execution-infrastructure', standalone: true, imports: [StatusIndicatorComponent],
  templateUrl: './execution-infrastructure.html', styleUrl: './execution-infrastructure.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExecutionInfrastructureComponent {
  private readonly http = inject(HttpClient);
  readonly reloadToken = input(0);
  readonly infrastructure = signal<ExecutionInfrastructure | null>(null);
  readonly error = signal(false);
  /** Engine and gate states on the shared indicator; a state the backend adds later stays neutral. */
  engineTone(state: string): StatusTone {
    return state === 'ready' ? 'success' : state === 'engine-not-live' ? 'danger' : 'neutral';
  }

  gateTone(reachability: string): StatusTone {
    return reachability === 'reachable' ? 'success' : reachability === 'unreachable' ? 'danger' : 'warning';
  }

  private readonly loadOnRefresh = effect(() => {
    this.reloadToken();
    this.http.get<ExecutionInfrastructure>('/api/system/execution-infrastructure').subscribe({
      next: value => { this.infrastructure.set(value); this.error.set(false); },
      error: () => { this.infrastructure.set(null); this.error.set(true); },
    });
  });
}
