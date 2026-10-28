import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { BoardSourceService, BoardSourceStatus } from '../../../../services/board-source.service';

const READINESS_LABELS: Record<BoardSourceStatus['readiness']['state'], string> = {
  'shadow-off': 'Shadow off',
  'no-comparisons': 'No comparison yet',
  'comparisons-stalled': 'Comparisons stalled',
  'clean-window-open': 'Clean window open',
  ready: 'Ready for task-server',
  switched: 'Task Server answers',
};

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
  imports: [DatePipe, DecimalPipe],
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

  readinessLabel(state: BoardSourceStatus['readiness']['state']): string {
    return READINESS_LABELS[state];
  }

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
