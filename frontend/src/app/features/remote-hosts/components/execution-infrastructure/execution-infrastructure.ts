import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';

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
  selector: 'app-execution-infrastructure', standalone: true,
  templateUrl: './execution-infrastructure.html', styleUrl: './execution-infrastructure.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExecutionInfrastructureComponent {
  private readonly http = inject(HttpClient);
  readonly reloadToken = input(0);
  readonly infrastructure = signal<ExecutionInfrastructure | null>(null);
  readonly error = signal(false);
  private readonly loadOnRefresh = effect(() => {
    this.reloadToken();
    this.http.get<ExecutionInfrastructure>('/api/system/execution-infrastructure').subscribe({
      next: value => { this.infrastructure.set(value); this.error.set(false); },
      error: () => { this.infrastructure.set(null); this.error.set(true); },
    });
  });
}
