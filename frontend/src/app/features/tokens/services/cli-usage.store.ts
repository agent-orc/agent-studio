import { DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { CliType } from '../../../models/task.model';
import { cliTypeIcon } from '../../../services/format.util';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../quota';
import { QuotaApiService, quotaProbeFailureLabel, quotaSnapshotIsStale } from '../../quota';
import { TokensApiService } from './tokens-api.service';
import type {
  AdHocUsageAggregate,
  TokenSummaryAggregate,
  TokenTimeline,
  WorkspaceExpensiveJob,
} from '../models/tokens.model';

/**
 * One derived per-CLI quota row: the strip's compact hover popover and
 * the CLI-Management detail panel both read this shape so the
 * at-a-glance number and the drill-in agree.
 */
export interface CliUsageQuotaRow {
  cliType: CliType;
  icon: string;
  label: string;
  plan: string | null;
  fetchedAt: string | null;
  freshness: string;
  stale: boolean;
  cliVersion?: string | null;
  probeFailedAt?: string | null;
  probeFailureLabel?: string | null;
  showingLastGood?: boolean;
  source: string | null;
  error: string | null;
  windows: QuotaWindow[];
  primary: QuotaWindow | null;
  primaryPct: number | null;
  primaryTone: 'ok' | 'warn' | 'hot' | 'unknown';
}

/**
 * Settings Token usage store for CLI quota probes and token aggregates.
 * Polling begins when the Token usage section opens; the footer uses the
 * separate UsageCockpitService subscription snapshot.
 *
 * - `ensureQuotaStarted()` starts a 60 s `/api/cli/quota` poll plus a 1 s
 *   relative-time tick for Settings token analysis. App-lifetime.
 * - `startDetail()` / `stopDetail()` ref-count the token-summary, ad-hoc,
 *   timeline, and expensive-job polls while the section is visible.
 */
@Injectable({ providedIn: 'root' })
export class CliUsageStore {
  private readonly quotaApi = inject(QuotaApiService);
  private readonly tokensApi = inject(TokensApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly hub = inject(JobsHubClient);

  readonly report = signal<QuotaReport | null>(null);
  readonly tokens = signal<TokenSummaryAggregate | null>(null);
  readonly adhoc = signal<AdHocUsageAggregate | null>(null);
  readonly timeline24h = signal<TokenTimeline | null>(null);
  readonly timeline7d = signal<TokenTimeline | null>(null);
  readonly expensiveJobs = signal<WorkspaceExpensiveJob[]>([]);
  readonly refreshing = signal<Record<string, boolean>>({});
  readonly refreshingAll = signal(false);
  readonly nowTick = signal(Date.now());

  readonly quotaRows = computed<CliUsageQuotaRow[]>(() => {
    const r = this.report();
    if (!r) return [];
    const ttlMs = (r.ttlSeconds ?? 600) * 1000;
    const now = this.nowTick();
    return r.snapshots.map(s => this.buildRow(s, ttlMs, now));
  });

  private quotaStarted = false;
  private detailConsumers = 0;
  private quotaPollTimer: VisibleIntervalHandle | null = null;
  private tokenPollTimer: VisibleIntervalHandle | null = null;
  private adhocPollTimer: VisibleIntervalHandle | null = null;
  private detailPollTimer: VisibleIntervalHandle | null = null;
  // tickTimer stays as raw setInterval - it's a 1 s relative-time
  // refresh; paused-on-hidden would show a stale "5 min ago" the moment
  // the user returns to the tab. Same exception as NowTickService.
  private tickTimer: ReturnType<typeof setInterval> | null = null;

  // Tracks the last-seen hub connection state so the reconnect effect only
  // re-hydrates on a genuine down→up transition, not on every signal read.
  private wasHubConnected = false;

  constructor() {
    this.destroyRef.onDestroy(() => {
      if (this.quotaPollTimer != null) clearVisibleInterval(this.quotaPollTimer);
      if (this.tickTimer != null) clearInterval(this.tickTimer);
      this.clearDetailTimers();
    });
  }

  /**
   * Re-hydrate on SignalR reconnect. The board re-pulls itself via the hub's
   * `reconnected` hook, but this store polls on its own 60 s cadence and would
   * otherwise leave the Settings token analysis showing pre-restart numbers
   * for up to a minute after the backend returns. Reconnect pulls immediately.
   */
  private readonly reconnectEffect = effect(() => {
    const up = this.hub.connected();
    untracked(() => {
      const reconnected = up && !this.wasHubConnected;
      this.wasHubConnected = up;
      if (!reconnected) return;
      if (this.quotaStarted) this.fetchQuota();
      if (this.detailConsumers > 0) {
        this.fetchTokensFresh();
        this.fetchAdHoc();
        this.fetchDetail();
      }
    });
  });

  /** Start the lightweight quota poll + relative-time tick (idempotent). */
  ensureQuotaStarted(): void {
    if (this.quotaStarted) return;
    this.quotaStarted = true;
    this.fetchQuota();
    this.quotaPollTimer = setVisibleInterval(() => this.fetchQuota(), 60_000);
    this.tickTimer = setInterval(() => this.nowTick.set(Date.now()), 1_000);
  }

  /**
   * Begin (or refresh) the heavy detail aggregates. Ref-counted so the
   * polls run only while at least one detail consumer is mounted.
   */
  startDetail(): void {
    this.ensureQuotaStarted();
    this.detailConsumers++;
    if (this.detailConsumers === 1) {
      this.fetchTokensCached();
      this.fetchTokensFresh();
      this.fetchAdHoc();
      this.fetchDetailCached();
      this.fetchDetail();
      this.tokenPollTimer = setVisibleInterval(() => this.fetchTokensFresh(), 30_000);
      this.adhocPollTimer = setVisibleInterval(() => this.fetchAdHoc(), 60_000);
      this.detailPollTimer = setVisibleInterval(() => this.fetchDetail(), 60_000);
    } else {
      // A second consumer mounted while polling is live - give it fresh
      // numbers without waiting for the next tick.
      this.fetchTokensFresh();
      this.fetchDetail();
    }
  }

  stopDetail(): void {
    if (this.detailConsumers === 0) return;
    this.detailConsumers--;
    if (this.detailConsumers === 0) this.clearDetailTimers();
  }

  refreshAll(): void {
    if (this.refreshingAll()) return;
    this.refreshingAll.set(true);
    this.quotaApi.refreshQuotaAll().subscribe({
      next: () => { this.fetchQuota(); this.refreshingAll.set(false); },
      error: () => this.refreshingAll.set(false),
    });
    this.fetchTokensFresh();
    this.fetchDetail();
  }

  refreshOne(cliType: CliType): void {
    if (this.refreshing()[cliType]) return;
    this.refreshing.update(m => ({ ...m, [cliType]: true }));
    this.quotaApi.refreshQuotaForCli(cliType).subscribe({
      next: () => {
        this.fetchQuota();
        this.refreshing.update(m => ({ ...m, [cliType]: false }));
      },
      error: () => this.refreshing.update(m => ({ ...m, [cliType]: false })),
    });
  }

  // ---- Data fetches ----

  fetchQuota(): void {
    this.quotaApi.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: () => { /* keep last value */ },
    });
  }

  private fetchTokensCached(): void {
    this.tokensApi.getTokenSummaryAggregateCached().subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.tokens.set(resp.body); },
      error: () => { /* tolerated */ },
    });
  }

  private fetchTokensFresh(): void {
    this.tokensApi.getTokenSummaryAggregate().subscribe({
      next: (a) => this.tokens.set(a),
      error: () => { /* keep last value */ },
    });
  }

  private fetchAdHoc(): void {
    this.tokensApi.getAdHocUsage().subscribe({
      next: (a) => this.adhoc.set(a),
      error: () => { /* keep last value */ },
    });
  }

  private fetchDetail(): void {
    this.tokensApi.getWorkspaceTokensTimeline(24, 60).subscribe({
      next: (t) => this.timeline24h.set(t),
      error: () => { /* keep last value */ },
    });
    this.tokensApi.getWorkspaceTokensTimeline(168, 60).subscribe({
      next: (t) => this.timeline7d.set(t),
      error: () => { /* keep last value */ },
    });
    this.tokensApi.getWorkspaceExpensiveJobs(8).subscribe({
      next: (r) => this.expensiveJobs.set(r.jobs ?? []),
      error: () => { /* keep last value */ },
    });
  }

  private fetchDetailCached(): void {
    this.tokensApi.getWorkspaceTokensTimelineCached(24, 60).subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.timeline24h.set(resp.body); },
      error: () => { /* tolerated */ },
    });
    this.tokensApi.getWorkspaceTokensTimelineCached(168, 60).subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.timeline7d.set(resp.body); },
      error: () => { /* tolerated */ },
    });
    this.tokensApi.getWorkspaceExpensiveJobsCached().subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.expensiveJobs.set(resp.body.jobs ?? []); },
      error: () => { /* tolerated */ },
    });
  }

  private clearDetailTimers(): void {
    if (this.tokenPollTimer != null) { clearVisibleInterval(this.tokenPollTimer); this.tokenPollTimer = null; }
    if (this.adhocPollTimer != null) { clearVisibleInterval(this.adhocPollTimer); this.adhocPollTimer = null; }
    if (this.detailPollTimer != null) { clearVisibleInterval(this.detailPollTimer); this.detailPollTimer = null; }
  }

  // ---- Derivation ----

  private buildRow(s: QuotaSnapshot, ttlMs: number, now: number): CliUsageQuotaRow {
    const fetchedMs = s.fetchedAt ? Date.parse(s.fetchedAt) : NaN;
    const ageMs = Number.isFinite(fetchedMs) ? Math.max(0, now - fetchedMs) : Number.POSITIVE_INFINITY;
    const stale = quotaSnapshotIsStale(s, ttlMs, now);
    const freshness = !s.fetchedAt ? 'never refreshed' : 'updated ' + this.formatAgo(ageMs);
    const primary = s.windows.length > 0
      ? [...s.windows].sort((a, b) => (b.usedPct ?? -1) - (a.usedPct ?? -1))[0]
      : null;
    const primaryPct = primary?.usedPct == null ? null : Math.round(primary.usedPct);
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label: this.cliLabel(s.cliType),
      plan: s.plan,
      fetchedAt: s.fetchedAt,
      stale,
      cliVersion: s.cliVersion ?? null,
      probeFailedAt: s.probeFailedAt ?? null,
      probeFailureLabel: quotaProbeFailureLabel(s),
      showingLastGood: !!s.probeFailedAt && (s.windows.length > 0 || !!s.plan),
      freshness,
      source: s.source,
      error: s.error,
      windows: s.windows,
      primary,
      primaryPct,
      primaryTone: this.toneFor(primaryPct),
    };
  }

  private toneFor(pct: number | null): 'ok' | 'warn' | 'hot' | 'unknown' {
    if (pct == null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

  private cliLabel(cli: string): string {
    switch (cli) {
      case 'claude': return 'Claude';
      case 'codex': return 'Codex';
      case 'gemini': return 'Gemini';
      default: return cli;
    }
  }

  private formatAgo(ms: number): string {
    if (!Number.isFinite(ms)) return 'never';
    const sec = Math.floor(ms / 1000);
    if (sec < 5) return 'just now';
    if (sec < 60) return `${sec} s ago`;
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min} min ago`;
    const hr = Math.floor(min / 60);
    if (hr < 24) return `${hr} h ago`;
    const d = Math.floor(hr / 24);
    return `${d} d ago`;
  }
}
