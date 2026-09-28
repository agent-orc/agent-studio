import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, signal, computed } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { CliType } from '../../../../models/task.model';
import { QuotaApiService } from '../../services/quota-api.service';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../models/quota.model';
import { cliTypeIcon } from '../../../../services/format.util';

import { TooltipDirective } from 'coding-agent-chat/shared';
/**
 * Compact strip surfacing each installed CLI's subscription quota / rate-limit
 * status — designed to live at the top of the CLI Sessions sidesheet so the
 * "how much budget do I have left" question is answerable at a glance.
 *
 * Behaviour:
 *  - On mount, calls `GET /api/cli/quota` which returns the cached report and
 *    triggers background re-probes for any stale entry. No spinner needed.
 *  - The "↻" buttons force a synchronous re-probe of one CLI (or all).
 *    These calls take several seconds because they spawn a fresh PTY.
 *  - Re-renders every second so reset countdowns stay live.
 *  - Bar colour: green < 70%, amber 70–90%, red > 90%.
 */
@Component({
  selector: 'app-quota-strip',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './quota-strip.html',
  styleUrl: './quota-strip.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuotaStripComponent implements OnInit, OnDestroy {
  private jobService = inject(TaskService);

  private readonly quotaApi = inject(QuotaApiService);
  readonly report = signal<QuotaReport | null>(null);
  readonly errorMsg = signal<string | null>(null);
  readonly refreshingAll = signal(false);
  // Per-CLI in-flight refresh flags so each card's spinner is independent.
  readonly refreshing = signal<Record<CliType, boolean>>({
    claude: false,
    codex: false,
    gemini: false,
  });
  // Tick once a second so countdown labels (`resets in 23m`) stay live without
  // re-fetching the snapshot.
  private readonly nowTick = signal(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;
  private autoHandle: ReturnType<typeof setInterval> | null = null;

  readonly snapshots = computed<QuotaSnapshot[]>(() => this.report()?.snapshots ?? []);
  readonly lastUpdate = computed(() => {
    const at = this.report()?.at;
    if (!at) return null;
    try {
      return new Date(at).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' });
    } catch {
      return null;
    }
  });

  ngOnInit() {
    this.load();
    this.tickHandle = setInterval(() => this.nowTick.set(Date.now()), 1000);
    // Pull a fresh cached report every 30s — backend handles its own staleness.
    this.autoHandle = setInterval(() => this.load(true), 30000);
  }

  ngOnDestroy() {
    if (this.tickHandle) clearInterval(this.tickHandle);
    if (this.autoHandle) clearInterval(this.autoHandle);
  }

  private load(silent = false) {
    if (!silent) this.errorMsg.set(null);
    this.quotaApi.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: (err) => {
        if (!silent) this.errorMsg.set(err.error?.error || err.message || 'Failed to load quota');
      },
    });
  }

  refreshAll() {
    this.refreshingAll.set(true);
    this.errorMsg.set(null);
    this.quotaApi.refreshQuotaAll().subscribe({
      next: (r) => {
        this.report.set(r);
        this.refreshingAll.set(false);
      },
      error: (err) => {
        this.errorMsg.set(err.error?.error || err.message || 'Quota refresh failed');
        this.refreshingAll.set(false);
      },
    });
  }

  refreshOne(cliType: CliType) {
    this.refreshing.update((m) => ({ ...m, [cliType]: true }));
    this.quotaApi.refreshQuotaForCli(cliType).subscribe({
      next: (snap) => {
        // Splice the updated snapshot back into the cached report.
        const cur = this.report();
        if (cur) {
          const next = {
            ...cur,
            snapshots: cur.snapshots.map((s) => (s.cliType === cliType ? snap : s)),
          };
          this.report.set(next);
        }
        this.refreshing.update((m) => ({ ...m, [cliType]: false }));
      },
      error: (err) => {
        this.errorMsg.set(err.error?.error || err.message || `Refresh failed for ${cliType}`);
        this.refreshing.update((m) => ({ ...m, [cliType]: false }));
      },
    });
  }

  cliLabel(t: CliType): string {
    switch (t) {
      case 'claude':
        return 'Claude Code';
      case 'codex':
        return 'Codex';
      case 'gemini':
        return 'Gemini';
    }
  }

  cliIcon(t: CliType): string {
    return cliTypeIcon(t);
  }

  formatPct(pct: number | null): string {
    if (pct === null || isNaN(pct)) return 'Unknown';
    return `${pct.toFixed(pct >= 10 ? 0 : 1)}%`;
  }

  /** Used percentage clamped to [0, 100] for the progress bar's visual width. */
  barWidth(pct: number | null): number {
    if (pct === null || isNaN(pct)) return 0;
    return Math.max(0, Math.min(100, pct));
  }

  severity(pct: number | null): 'ok' | 'warn' | 'crit' {
    if (pct === null || isNaN(pct)) return 'ok';
    if (pct >= 90) return 'crit';
    if (pct >= 70) return 'warn';
    return 'ok';
  }

  /**
   * Live human-friendly "in 1h 23m" / "in 4d 3h" countdown when we have a
   * concrete reset timestamp; otherwise falls back to whatever the backend
   * already formatted (e.g. "May 1", "3:40am (Europe/Berlin)").
   */
  resetText(w: QuotaWindow): string | null {
    if (w.resetAt) {
      // Use the ticking signal as the time base — NOT Date.now() — so the
      // value is stable within a single change-detection cycle. Reading
      // Date.now() directly causes NG0100 when the wall clock crosses a
      // minute boundary between the dev-mode "check" and "verify" passes.
      const now = this.nowTick();
      const target = Date.parse(w.resetAt);
      if (!isNaN(target)) {
        const ms = target - now;
        if (ms > 0) {
          const fallback = w.resetLabel ? ` (${w.resetLabel})` : '';
          return `in ${this.formatDuration(ms)}${fallback}`;
        }
      }
    }
    return w.resetLabel ?? null;
  }

  private formatDuration(ms: number): string {
    const sec = Math.floor(ms / 1000);
    const days = Math.floor(sec / 86400);
    const hours = Math.floor((sec % 86400) / 3600);
    const mins = Math.floor((sec % 3600) / 60);
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${mins}m`;
    if (mins > 0) return `${mins}m`;
    return `${sec}s`;
  }
}
