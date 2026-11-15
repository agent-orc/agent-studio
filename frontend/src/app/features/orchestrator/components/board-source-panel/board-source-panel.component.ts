import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import { BoardSourceService, BoardSourceStatus } from '../../../../services/board-source.service';

type ReadinessState = BoardSourceStatus['readiness']['state'];

const READINESS: Record<ReadinessState, { label: string; tone: StatusTone }> = {
  'shadow-off': { label: 'Shadow off', tone: 'neutral' },
  'no-comparisons': { label: 'No comparison yet', tone: 'neutral' },
  'comparisons-stalled': { label: 'Comparisons stalled', tone: 'warning' },
  'clean-window-open': { label: 'Clean window open', tone: 'running' },
  ready: { label: 'Ready for task-server', tone: 'success' },
  switched: { label: 'Task Server answers', tone: 'success' },
};

/** Tone of the mirror sync fact: synced, waiting for the first sync, or off. */
export function mirrorSyncTone(status: Pick<BoardSourceStatus, 'mirroring' | 'sync'>): StatusTone {
  if (status.sync.syncedGeneration !== null) return 'success';
  return status.mirroring ? 'warning' : 'neutral';
}

/**
 * Metrics card of the board read model (pipeline delivery target M1 and M2):
 * the configured Board:Source, the Task Server mirror, the board read p95
 * and Git spawns per request against their targets, the shadow differences
 * by card and field, and the 7-day cutover gate. An operator explains a
 * difference inline; explained differences no longer hold the gate.
 */
@Component({
  selector: 'app-board-source-panel',
  standalone: true,
  imports: [DatePipe, DecimalPipe, StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './board-source-panel.component.html',
  styleUrl: './board-source-panel.component.scss',
})
export class BoardSourcePanelComponent implements OnInit {
  private readonly api = inject(BoardSourceService);
  readonly status = this.api.status;
  readonly loadError = this.api.loadError;
  readonly explaining = signal<string | null>(null);
  readonly explanation = signal('');

  ngOnInit(): void {
    void this.api.load();
  }

  refresh(): void {
    void this.api.load();
  }

  readinessLabel(state: ReadinessState): string {
    return READINESS[state]?.label ?? state;
  }

  readinessTone(state: ReadinessState): StatusTone {
    return READINESS[state]?.tone ?? 'neutral';
  }

  readonly mirrorSyncTone = mirrorSyncTone;

  startExplain(fingerprint: string): void {
    this.explaining.set(fingerprint);
    this.explanation.set('');
  }

  async saveExplanation(): Promise<void> {
    const fingerprint = this.explaining();
    const text = this.explanation().trim();
    if (!fingerprint || !text) return;
    await this.api.explain(fingerprint, text);
    this.explaining.set(null);
  }
}
