import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  output,
  signal,
  untracked,
} from '@angular/core';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { CliType } from '../../../../models/task.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../models/quota.model';
import { cliTypeIcon } from '../../../../services/format.util';
import { QuotaApiService } from '../../services/quota-api.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { formatQuotaAge, quotaProbeFailureLabel, quotaSnapshotIsStale } from '../../quota-freshness.util';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { AuthService } from '../../../../services/auth.service';
import { ProviderAuthStatusService } from '../../../remote-hosts/services/provider-auth-status.service';
import { runnerQuotaCard } from './runner-quota-card';

type Tone = 'ok' | 'warn' | 'hot' | 'unknown';

interface QuotaWindowDisplay {
  value: string;
  barPct: number;
  tone: Tone;
  windowKind: 'five_hour' | 'weekly';
}

/**
 * One rendered usage pill inside a card: a short window tag (5H / WK /
 * MO …), the used%, and a small bar. Claude and Codex report both a
 * 5-hour rolling window and a weekly window, so their cards now carry
 * two chips side by side instead of collapsing to a single primary; a
 * CLI that only exposes one window (Copilot's monthly) renders one chip.
 */
interface QuotaChip {
  windowKey: string;
  tag: string;
  label?: string;
  value: string;
  barPct: number;
  tone: Tone;
}

/**
 * The single most-constraining window. Still used as the fallback chip
 * for CLIs whose constraining window is neither a 5H nor a WK bucket.
 */
interface QuotaPrimaryDisplay {
  value: string;
  tag: string;
  barPct: number;
  hasValue: boolean;
  tone: Tone;
}

/**
 * Semantic state of a CLI quota card. Drives the highlight reading
 * for the operator: idle = quiet, warn = approaching threshold, hot =
 * over threshold, stale = snapshot older than TTL, unavailable = no
 * data, error = probe failed.
 */
type QuotaCardState = 'idle' | 'warn' | 'hot' | 'stale' | 'unavailable' | 'error';

export interface QuotaCardModel {
  cliType: CliType;
  icon: string;
  label: string;
  ariaLabel: string;
  chips: QuotaChip[];
  tone: Tone;
  state: QuotaCardState;
  fetchedAt: string | null;
  stale: boolean;
  freshness: string;
  windows: QuotaWindow[];
  error: string | null;
  probeFailureLabel: string | null;
  source: string | null;
}

/**
 * Compact CLI-quota row for the app status bar. One card per primary
 * routing CLI; each card surfaces every reported usage window as its own
 * chip (Claude and Codex therefore show 5H and WK side by side). The
 * cards are buttons: clicking one opens that CLI's own usage-detail modal
 * (one modal per CLI, no shared hover tooltip and no grouped view).
 *
 * The strip reads the quota report from the backend's filesystem-cached
 * store on app start (no spinner; data appears immediately). Stale
 * snapshots show a small dot + border tint so the user knows the number
 * is from the previous run.
 */
@Component({
  selector: 'app-header-quota',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './header-quota.html',
  styleUrl: './header-quota.scss'
})
export class HeaderQuotaComponent implements OnInit, OnDestroy {
  private readonly quotaApi = inject(QuotaApiService);
  private readonly auth = inject(AuthService);
  private readonly providerAuth = inject(ProviderAuthStatusService);
  readonly networked = computed(() => this.auth.status()?.profile === 'networked');
  private readonly hub = inject(JobsHubClient);
  readonly report = signal<QuotaReport | null>(null);
  /** Re-evaluated every second so the freshness label ticks live. */
  readonly nowTick = signal(Date.now());
  readonly displayedCliTypes: CliType[] = ['claude', 'codex'];

  /** Emitted when a card is clicked: the host opens that CLI's modal. */
  readonly cliSelected = output<CliType>();

  private pollTimer: VisibleIntervalHandle | null = null;
  // tickTimer stays raw - 1 s relative-time refresh; pause-on-hidden
  // would show stale "5 min ago" the moment the user comes back.
  private tickTimer: ReturnType<typeof setInterval> | null = null;
  private pollingStarted = false;
  private wasHubConnected = false;

  /**
   * The status-bar quota strip owns its own lightweight cached report signal.
   * Its 60 s poll is intentionally cheap, but after a backend restart the
   * operator should not stare at the pre-restart quota snapshot until the next
   * timer tick. Refresh immediately when the jobs hub returns.
   */
  private readonly reconnectEffect = effect(() => {
    const up = this.hub.connected();
    untracked(() => {
      const reconnected = up && !this.wasHubConnected;
      this.wasHubConnected = up;
      if (reconnected && this.pollingStarted) this.fetch();
    });
  });

  readonly cards = computed<QuotaCardModel[]>(() => {
    if (this.networked()) return this.displayedCliTypes.map(cli =>
      runnerQuotaCard(cli, this.cliLabel(cli), this.providerAuth.statuses().filter(badge => badge.provider === cli)));
    const r = this.report();
    if (!r) return this.displayedCliTypes.map(cli => this.emptyCard(cli));
    const ttlMs = (r.ttlSeconds ?? 600) * 1000;
    const now = this.nowTick();
    return this.displayedCliTypes.map(cli => {
      const snap = r.snapshots.find(s => s.cliType === cli);
      return snap ? this.buildCard(snap, ttlMs, now) : this.emptyCard(cli);
    });
  });

  ngOnInit(): void {
    this.pollingStarted = true;
    this.wasHubConnected = this.hub.connected();
    if (!this.networked()) this.fetch();
    // Poll the backend every 60s. The backend serves from cache and
    // background-refreshes stale entries, so we get fresh data without
    // forcing a re-probe.
    this.pollTimer = setVisibleInterval(() => this.fetch(), 60_000);
    this.tickTimer = setInterval(() => this.nowTick.set(Date.now()), 1_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    if (this.tickTimer != null) clearInterval(this.tickTimer);
  }

  select(cliType: CliType): void {
    if (this.networked()) return;
    this.cliSelected.emit(cliType);
  }

  fetch(): void {
    if (this.networked()) return;
    this.quotaApi.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: () => { /* keep last value, do not clear */ }
    });
  }


  private buildCard(s: QuotaSnapshot, ttlMs: number, now: number): QuotaCardModel {
    const fetchedMs = s.fetchedAt ? Date.parse(s.fetchedAt) : NaN;
    const ageMs = Number.isFinite(fetchedMs) ? Math.max(0, now - fetchedMs) : Number.POSITIVE_INFINITY;
    const stale = quotaSnapshotIsStale(s, ttlMs, now);
    const freshness = !s.fetchedAt
      ? 'never refreshed'
      : 'updated ' + formatQuotaAge(ageMs);
    const label = this.cliLabel(s.cliType);
    const shortWindow = this.buildWindowDisplay(s.windows, 'five_hour');
    const weekWindow = this.buildWindowDisplay(s.windows, 'weekly');
    const primary = this.buildPrimaryDisplay(s.windows);
    const chips = this.buildChips(shortWindow, weekWindow, primary, s.windows);
    const probeFailureLabel = s.windows.length > 0 || !!s.plan
      ? quotaProbeFailureLabel(s)
      : null;
    const unavailableError = !!s.error && s.windows.length === 0;
    const tone = this.cardTone(shortWindow, weekWindow, unavailableError, primary);
    const state = this.cardState(tone, stale, unavailableError, shortWindow, weekWindow, primary);
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label,
      ariaLabel: [this.cardAriaLabel(label, chips), probeFailureLabel].filter(Boolean).join('. '),
      chips,
      tone,
      state,
      fetchedAt: s.fetchedAt,
      stale,
      freshness,
      windows: s.windows,
      error: s.error,
      probeFailureLabel,
      source: s.source
    };
  }

  private emptyCard(cliType: CliType): QuotaCardModel {
    const label = this.cliLabel(cliType);
    return {
      cliType,
      icon: cliTypeIcon(cliType),
      label,
      ariaLabel: `${label} quota: no data yet`,
      chips: [this.placeholderChip()],
      tone: 'unknown',
      state: 'unavailable',
      fetchedAt: null,
      stale: true,
      freshness: 'never refreshed',
      windows: [],
      error: null,
      probeFailureLabel: null,
      source: null
    };
  }

  /**
   * Build the chips a card renders. Claude / Codex expose a 5H and a WK
   * window, so both are shown; a CLI that exposes neither (Copilot's
   * monthly) falls back to its single most-constraining window so the
   * card never renders empty.
   */
  private buildChips(
    sw: QuotaWindowDisplay | undefined,
    ww: QuotaWindowDisplay | undefined,
    primary: QuotaPrimaryDisplay,
    windows?: QuotaWindow[],
  ): QuotaChip[] {
    if (windows?.length) return this.buildReportedWindowChips(windows);

    const chips: QuotaChip[] = [];
    if (sw) chips.push({ windowKey: '5h', tag: '5H', value: sw.value, barPct: sw.barPct, tone: sw.tone });
    if (ww) chips.push({ windowKey: 'wk', tag: 'WK', value: ww.value, barPct: ww.barPct, tone: ww.tone });
    if (chips.length > 0) return chips;
    if (primary.hasValue) {
      return [{ windowKey: 'primary', tag: primary.tag, value: primary.value, barPct: primary.barPct, tone: primary.tone }];
    }
    return [this.placeholderChip()];
  }

  private placeholderChip(): QuotaChip {
    return { windowKey: 'none', tag: '', value: '—', barPct: 0, tone: 'unknown' };
  }

  private buildReportedWindowChips(windows: QuotaWindow[]): QuotaChip[] {
    const seen = new Map<string, number>();
    return windows.map((w) => {
      const baseKey = this.windowKey(w.label);
      const count = seen.get(baseKey) ?? 0;
      seen.set(baseKey, count + 1);
      const pct = this.effectiveUsedPct(w);
      const windowKey = count === 0 ? baseKey : `${baseKey}-${count + 1}`;
      return {
        windowKey,
        tag: this.windowTag(w.label),
        label: w.label,
        value: pct == null ? 'Unknown' : `${pct}%`,
        barPct: Math.max(0, Math.min(100, pct ?? 0)),
        tone: this.toneFor(pct),
      };
    });
  }

  private buildWindowDisplay(windows: QuotaWindow[], kind: 'five_hour' | 'weekly'): QuotaWindowDisplay | undefined {
    const w = this.findWindow(windows, kind);
    if (!w) return undefined;
    const pct = this.effectiveUsedPct(w);
    const tone = this.toneFor(pct);
    const value = pct == null ? '—' : `${pct}%`;
    const barPct = Math.max(0, Math.min(100, pct ?? 0));
    return { value, barPct, tone, windowKind: kind };
  }

  private findWindow(windows: QuotaWindow[], kind: 'five_hour' | 'weekly'): QuotaWindow | null {
    for (const w of windows) {
      const lower = (w.label ?? '').toLowerCase();
      if (kind === 'five_hour' && (lower.includes('5h') || lower.includes('5-hour') || lower.includes('session'))) return w;
      if (kind === 'weekly' && (lower.includes('weekly') || lower.includes('week'))) return w;
    }
    return null;
  }

  private cardTone(
    sw: QuotaWindowDisplay | undefined,
    ww: QuotaWindowDisplay | undefined,
    hasError: boolean,
    primary: QuotaPrimaryDisplay,
  ): Tone {
    if (hasError && !sw && !ww && !primary.hasValue) return 'unknown';
    const tones: Tone[] = [];
    if (sw) tones.push(sw.tone);
    if (ww) tones.push(ww.tone);
    // The primary covers windows the 5H / WK lookup misses (e.g.
    // Copilot's monthly premium-request window) so the card highlight
    // never goes quiet just because the constraining window isn't a
    // session/weekly bucket.
    if (primary.hasValue) tones.push(primary.tone);
    if (tones.length === 0) return 'unknown';
    if (tones.includes('hot')) return 'hot';
    if (tones.includes('warn')) return 'warn';
    return 'ok';
  }

  private buildPrimaryDisplay(windows: QuotaWindow[]): QuotaPrimaryDisplay {
    const ranked = [...windows].sort((a, b) => (this.effectiveUsedPct(b) ?? -1) - (this.effectiveUsedPct(a) ?? -1));
    const w = ranked[0];
    const pct = w ? this.effectiveUsedPct(w) : null;
    if (!w || pct == null) {
      return { value: '—', tag: w ? this.windowTag(w.label) : '', barPct: 0, hasValue: false, tone: 'unknown' };
    }
    return {
      value: `${pct}%`,
      tag: this.windowTag(w.label),
      barPct: Math.max(0, Math.min(100, pct)),
      hasValue: true,
      tone: this.toneFor(pct),
    };
  }

  /** Short uppercase tag for a window label, e.g. "5H", "WK", "MO". */
  private windowTag(label: string): string {
    const l = (label ?? '').toLowerCase();
    const isSpark = l.includes('spark');
    const isFiveHour = l.includes('5h') || l.includes('5-hour') || l.includes('session');
    const isWeekly = l.includes('week');
    if (isSpark && isFiveHour) return 'S5H';
    if (isSpark && isWeekly) return 'SWK';
    if (l.includes('5h') || l.includes('5-hour') || l.includes('session')) return '5H';
    if (l.includes('week')) return 'WK';
    if (l.includes('month')) return 'MO';
    if (l.includes('dai') || l.includes('day')) return 'DY';
    if (l.includes('hour')) return 'HR';
    const word = (label ?? '').trim().split(/\s+/)[0] ?? '';
    return word.slice(0, 2).toUpperCase();
  }

  private windowKey(label: string): string {
    const l = (label ?? '').toLowerCase();
    const isSpark = l.includes('spark');
    const isFiveHour = l.includes('5h') || l.includes('5-hour') || l.includes('session');
    const isWeekly = l.includes('week');
    if (isSpark && isFiveHour) return 'spark-5h';
    if (isSpark && isWeekly) return 'spark-wk';
    if (isFiveHour) return '5h';
    if (isWeekly) return 'wk';
    if (l.includes('month')) return 'mo';
    if (l.includes('dai') || l.includes('day')) return 'dy';
    if (l.includes('hour')) return 'hr';
    return l.replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'window';
  }

  private cardAriaLabel(label: string, chips: QuotaChip[]): string {
    const real = chips.filter(c => c.windowKey !== 'none');
    if (real.length === 0) return `${label} quota: no data yet`;
    const parts = real.map(c => {
      const name = c.label ?? c.tag;
      return `${name ? name + ' ' : ''}${c.value}${c.value === 'Unknown' ? '' : ' used'}`;
    });
    return `${label} quota: ${parts.join(', ')}`;
  }

  /**
   * Effective used-percentage for a window, applying the operator's
   * "%-limit" rule so a card is never dropped or blanked just because a
   * CLI omits an explicit numeric cap:
   *
   *  - `usedPct` set (Codex / Claude report this directly) wins.
   *  - A numeric `used` + `limit` pair is turned into a percentage.
   *  - unit `"%"` with a null `limit` means the limit IS 100% (the CLI
   *    says "66% used", not "66 of null"), so a `used` given under that
   *    unit is already the progress against 100.
   *
   * Returns null only when the window carries no usable number at all, so
   * a fresh Codex snapshot (4 windows, `used`/`limit` null, `usedPct` set)
   * always yields real chips instead of empty "—" placeholders.
   */
  private effectiveUsedPct(w: QuotaWindow): number | null {
    if (w.usedPct != null) return Math.round(w.usedPct);
    if (w.used != null && w.limit != null && w.limit > 0) {
      return Math.round((w.used / w.limit) * 100);
    }
    if (w.unit === '%' && w.used != null) return Math.round(w.used);
    return null;
  }

  private toneFor(pct: number | null): Tone {
    if (pct === null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

  private cardState(
    tone: Tone,
    stale: boolean,
    hasError: boolean,
    sw: QuotaWindowDisplay | undefined,
    ww: QuotaWindowDisplay | undefined,
    primary: QuotaPrimaryDisplay,
  ): QuotaCardState {
    if (hasError) return 'error';
    if (!sw && !ww && !primary.hasValue) return 'unavailable';
    if (tone === 'hot') return 'hot';
    if (tone === 'warn') return 'warn';
    if (stale) return 'stale';
    return 'idle';
  }

  private cliLabel(cli: string): string {
    switch (cli) {
      case 'claude': return 'Claude';
      case 'codex': return 'Codex';
      case 'gemini': return 'Gemini';
      default: return cli;
    }
  }

}
