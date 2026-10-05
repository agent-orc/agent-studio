import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { catchError, forkJoin, of, switchMap } from 'rxjs';
import type { CliType } from '../../../../models/task.model';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { QuotaHistoryResponse, QuotaHistoryWindow } from '../../models/quota-history.model';
import { QuotaApiService, type CliModelRoutesResponse } from '../../services/quota-api.service';
import { QuotaCurveComponent } from '../quota-curve/quota-curve';
import {
  type QuotaFallbackStatus,
  forecastSentence,
  formatRate,
  quotaFallbackStatus,
  quotaTone,
} from '../../quota-forecast.util';

const HISTORY_HOURS = 48;
const REFRESH_MS = 5 * 60_000;

interface WeeklyRow {
  window: QuotaHistoryWindow;
  sentence: string;
  warn: boolean;
  fallback: QuotaFallbackStatus | null;
}

interface ForecastCard {
  cliType: CliType;
  from: string;
  to: string;
  session: QuotaHistoryWindow | null;
  weekly: WeeklyRow[];
}

/**
 * Quota forecast section of CLI Management (AGT-3001). Per CLI: the weekly
 * curve of the last 48 hours, the current rate over the last 3 hours, the
 * forecast time of 100%, and the reset time; the session window as a small
 * gauge. When a weekly window is forecast to reach 100% before its reset, the
 * configured fallback from `/api/cli/quota/model-routes` is named with whether
 * it is armed. All numbers come from `/api/cli/quota/history`; this component
 * only lays them out.
 */
@Component({
  selector: 'app-quota-forecast-panel',
  standalone: true,
  imports: [QuotaCurveComponent],
  templateUrl: './quota-forecast-panel.html',
  styleUrl: './quota-forecast-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuotaForecastPanelComponent implements OnInit {
  private readonly quotaApi = inject(QuotaApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly histories = signal<QuotaHistoryResponse[]>([]);
  readonly routes = signal<CliModelRoutesResponse | null>(null);
  readonly loaded = signal(false);

  readonly cards = computed<ForecastCard[]>(() => {
    const routes = this.routes();
    return this.histories()
      .filter(history => history.windows.length > 0)
      .map(history => {
        const nowMs = Date.parse(history.to);
        return {
          cliType: history.cliType as CliType,
          from: history.from,
          to: history.to,
          session: history.windows.find(w => w.kind === 'session') ?? null,
          weekly: history.windows
            .filter(w => w.kind === 'weekly')
            .map(window => ({
              window,
              sentence: forecastSentence(window.forecast, nowMs, iso => this.when(iso)),
              warn: window.forecast.reachesFullBeforeReset,
              fallback: window.forecast.reachesFullBeforeReset
                ? quotaFallbackStatus(routes, history.cliType, window.label)
                : null,
            })),
        };
      });
  });

  ngOnInit(): void {
    this.reload();
    const handle = setInterval(() => this.reload(), REFRESH_MS);
    this.destroyRef.onDestroy(() => clearInterval(handle));
  }

  reload(): void {
    this.loading.set(true);
    this.quotaApi.getQuotaReport().pipe(
      switchMap(report => {
        const clis = [...new Set(report.snapshots.map(s => s.cliType))];
        return forkJoin({
          histories: clis.length === 0
            ? of([] as (QuotaHistoryResponse | null)[])
            : forkJoin(clis.map(cli => this.quotaApi.getQuotaHistory(cli, HISTORY_HOURS).pipe(catchError(() => of(null))))),
          routes: this.quotaApi.getModelRoutes().pipe(catchError(() => of(null))),
        });
      }),
    ).subscribe({
      next: ({ histories, routes }) => {
        this.histories.set(histories.filter((h): h is QuotaHistoryResponse => h !== null));
        this.routes.set(routes);
        this.errorMsg.set(histories.some(h => h === null) ? 'Some quota histories could not be loaded.' : null);
        this.loading.set(false);
        this.loaded.set(true);
      },
      error: () => {
        this.errorMsg.set('Quota history is unavailable.');
        this.loading.set(false);
        this.loaded.set(true);
      },
    });
  }

  icon(t: CliType): string { return cliTypeIcon(t); }
  label(t: CliType): string { return cliTypeLabel(t); }
  rate(value: number | null): string { return formatRate(value); }
  tone(pct: number | null): string { return quotaTone(pct); }

  pct(value: number | null): string {
    return value == null ? 'unknown' : `${Math.round(value)}%`;
  }

  gaugeWidth(value: number | null): number {
    return value == null ? 0 : Math.max(0, Math.min(100, value));
  }

  when(iso: string | null): string {
    if (!iso) return 'unknown';
    return new Date(iso).toLocaleString('en-US', {
      weekday: 'short', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false,
    });
  }
}
