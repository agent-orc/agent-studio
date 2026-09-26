import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, interval, of, startWith, switchMap, tap } from 'rxjs';

export interface ChatLiveUsageRow {
  project: string;
  host: string;
  queuedTurns: number;
  activeTurns: number;
  heavyTurns: number;
  heavyAccountingUnknown: boolean;
  cpuShare: number;
  cpuShareUnknown: boolean;
}

@Component({
  selector: 'app-chat-live-usage',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-live-usage.component.html',
  styleUrl: './chat-live-usage.component.scss',
})
export class ChatLiveUsageComponent {
  private readonly http = inject(HttpClient);
  readonly rows = signal<readonly ChatLiveUsageRow[]>([]);
  readonly unavailable = signal(false);
  readonly activeRows = computed(() => this.rows().filter(row => row.queuedTurns > 0 || row.activeTurns > 0));

  constructor() {
    interval(10_000).pipe(
      startWith(0),
      switchMap(() => this.http.get<{ items: ChatLiveUsageRow[] }>('/api/runner/project-chat/usage')
        .pipe(tap(() => this.unavailable.set(false)), catchError(() => {
          this.unavailable.set(true);
          return of({ items: [] });
        }))),
      takeUntilDestroyed(),
    ).subscribe(response => {
      this.rows.set(response.items);
    });
  }
}
