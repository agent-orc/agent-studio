import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import type { TaskInfo } from '../../../../models/task.model';
import type { StallInterventionRow, StallInterventionsResponse } from '../../../remote-hosts/models/stall-intervention.model';

/** Receipt and guarded Undo for a card parked by the Auto Review watchdog. */
@Component({
  selector: 'app-watchdog-receipt',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './watchdog-receipt.component.html',
  styleUrl: './watchdog-receipt.component.scss',
})
export class WatchdogReceiptComponent {
  readonly info = input.required<TaskInfo>();
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private loadedKey: string | null = null;
  readonly receipt = signal<StallInterventionRow | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);

  private readonly loadEffect = effect(() => {
    const info = this.info();
    const key = `${info.id}:${info.parkedBlocker?.parkedAt ?? ''}`;
    if (key === this.loadedKey) return;
    this.loadedKey = key;
    this.receipt.set(null);
    this.error.set(null);
    this.http.get<StallInterventionsResponse>('/api/watcher/interventions', {
      params: new HttpParams().set('card', info.id).set('limit', 50),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => {
        if (this.loadedKey !== key) return;
        this.receipt.set((response.interventions ?? []).find(row => row.kind === 'auto-review-stranded') ?? null);
      },
      error: () => this.error.set('The watchdog receipt could not be loaded.'),
    });
  });

  undo(): void {
    const row = this.receipt();
    if (!row?.receipt.undoable || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.http.post<{ receipt: StallInterventionRow['receipt'] }>(
      `/api/watcher/interventions/${encodeURIComponent(row.receipt.id)}/undo`, {},
    ).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: response => {
        this.busy.set(false);
        this.receipt.set({ ...row, receipt: response.receipt });
      },
      error: error => {
        this.busy.set(false);
        this.error.set(error?.error?.error === 'card-changed-since-park'
          ? 'The card changed since the watchdog parked it. Reload before undoing.'
          : 'Undo failed.');
      },
    });
  }
}
