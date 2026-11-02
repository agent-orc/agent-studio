import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export type GateDurationClass = 'unknown' | 'short' | 'long';

export interface GateSlotBoardSlot {
  slot: number;
  shortSlot: boolean;
  project: string | null;
  gateId: string | null;
  durationClass: GateDurationClass | null;
  since: string | null;
}

export interface GateSlotBoardWaiter {
  project: string;
  gateId: string;
  durationClass: GateDurationClass;
  since: string;
  reason: string;
}

export interface GateSlotBoard {
  total: number;
  shortSlot: number | null;
  rule: string;
  slots: GateSlotBoardSlot[];
  waiting: GateSlotBoardWaiter[];
}

/** Which remote gate slot runs what and why a gate waits (AGT-3423). */
@Component({
  selector: 'app-gate-slot-board',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gate-slot-board.html',
  styleUrl: './gate-slot-board.scss',
})
export class GateSlotBoardComponent implements OnDestroy {
  /** The project whose gates are marked on the shared board. */
  readonly projectName = input.required<string>();
  readonly board = signal<GateSlotBoard | null>(null);
  readonly busy = computed(() => this.board()?.slots.filter(slot => slot.project !== null).length ?? 0);
  private readonly http = inject(HttpClient);
  private readonly poll = setInterval(() => this.refresh(), 15_000);

  constructor() {
    this.refresh();
  }

  ngOnDestroy(): void {
    clearInterval(this.poll);
  }

  classLabel(value: GateDurationClass | null): string {
    return value === 'short' ? 'Short' : value === 'long' ? 'Long' : 'Unclassified';
  }

  minutesSince(iso: string | null): string {
    if (!iso) return '';
    const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(iso)) / 60_000));
    return `${minutes} min`;
  }

  private refresh(): void {
    this.http.get<GateSlotBoard>('/api/system/gate-slots').subscribe({
      next: board => {
        if (board && typeof board.total === 'number' && typeof board.rule === 'string'
            && Array.isArray(board.slots) && Array.isArray(board.waiting)) this.board.set(board);
      },
      error: () => { /* additive signal: keep the last known board */ },
    });
  }
}
