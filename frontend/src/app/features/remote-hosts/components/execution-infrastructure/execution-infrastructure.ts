import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

interface ExecutionInfrastructure {
  gate: { host: string | null; transport?: 'pull' | 'ssh'; busy: number; total: number; reachability: string;
    capacity: GateCapacity; capacitySource: 'configuration' | 'stored';
    lastReachedAt: string | null; lastFailureAt: string | null;
    workers?: readonly { id: string; name: string; slots: number; busy: number;
      lastHeartbeat: string; currentRun: string | null }[] };
  taskServer: string;
  engine: { state: string; required: boolean; lastContact: string | null; lastEngineId: string | null };
  workstation: string;
}

interface GateCapacity { slots: number; processorsPerGate: number; hostCores: number | null;
  allowOversubscription: boolean }

/** Runtime infrastructure outside the runner client registry. */
@Component({
  selector: 'app-execution-infrastructure', standalone: true, imports: [StatusIndicatorComponent, FormsModule],
  templateUrl: './execution-infrastructure.html', styleUrl: './execution-infrastructure.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ExecutionInfrastructureComponent {
  private readonly http = inject(HttpClient);
  readonly reloadToken = input(0);
  readonly infrastructure = signal<ExecutionInfrastructure | null>(null);
  readonly error = signal(false);
  readonly draft = signal<GateCapacity | null>(null);
  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly saved = signal(false);
  update<K extends keyof GateCapacity>(key: K, value: GateCapacity[K]): void {
    this.draft.update(current => current ? { ...current, [key]: value } : current);
    this.saved.set(false);
    this.saveError.set(null);
  }

  save(): void {
    const draft = this.draft();
    if (!draft || this.saving()) return;
    this.saving.set(true);
    this.http.put<{ capacity: GateCapacity }>('/api/system/gate-capacity', draft).subscribe({
      next: response => {
        this.infrastructure.update(current => current ? {
          ...current, gate: { ...current.gate, capacity: response.capacity,
            capacitySource: 'stored' },
        } : current);
        this.draft.set(response.capacity);
        this.saving.set(false);
        this.saved.set(true);
      },
      error: response => {
        this.saveError.set(response.error?.error ?? 'Gate capacity could not be saved.');
        this.saving.set(false);
      },
    });
  }
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
      next: value => { this.infrastructure.set(value); this.draft.set(value.gate.capacity); this.error.set(false); },
      error: () => { this.infrastructure.set(null); this.error.set(true); },
    });
  });
}
