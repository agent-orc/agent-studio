import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { TokenTimeline, TokenTimelineCell } from '../../../../features/tokens';
import { TokensApiService } from '../../../../features/tokens';
import { formatUsageCurrency, formatUsageTokens } from '../../usage-number-format.util';

const STORAGE_DISABLED_KEY = 'workspaceTokens.disabledProjects';
const STORAGE_WINDOW_KEY = 'workspaceTokens.windowHours';

type WindowHours = 1 | 6 | 24 | 168;

interface BucketBar {
  bucketStart: string;
  bucketEnd: string;
  total: number;
  segments: BucketSegment[];
  xPct: number;
  widthPct: number;
}

interface BucketSegment {
  project: string;
  cell: TokenTimelineCell;
  total: number;
  yPct: number;
  hPct: number;
  color: string;
}

/**
 * Workspace-wide token-usage timeline. One central view, segmented by
 * project, plotted on a time axis. Sourced from the orchestrator log
 * via `GET /api/workspace/tokens/timeline` (cheap, no recompute).
 *
 * Chart shape: stacked bars per bucket. The x-axis is wall-clock time
 * across the selected window (1 / 6 / 24 / 168 hours); y-axis is total
 * tokens (input + output + cache read + cache write). Each bar is split
 * vertically into one segment per project, coloured from a stable
 * hue palette so a project keeps the same colour across reloads.
 *
 * Project legend toggles cells on/off (saved per-window in localStorage)
 * so a busy project does not crowd out a quieter one. Hover any segment
 * to reveal a popover with the cell's full record (calls, per-stream
 * tokens, dollars when known).
 */
@Component({
  selector: 'app-workspace-token-timeline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-token-timeline.html',
  styleUrl: './workspace-token-timeline.scss'
})
export class WorkspaceTokenTimelineComponent implements OnInit, OnDestroy {
  private readonly tokensApi = inject(TokensApiService);
  private readonly jobService = inject(TaskService);

  // Hard-coded SVG canvas. CSS scales the rendered box; the viewBox keeps
  // the math simple and lets the chart resize without a ResizeObserver.
  readonly svgW = 800;
  readonly svgH = 220;
  readonly padL = 36;
  readonly padR = 8;
  readonly padT = 8;
  readonly padB = 18;

  readonly windowOptions: { hours: WindowHours; label: string; testId: string }[] = [
    { hours: 1,   label: '1h',  testId: '1h' },
    { hours: 6,   label: '6h',  testId: '6h' },
    { hours: 24,  label: '24h', testId: '24h' },
    { hours: 168, label: '7d',  testId: '7d' },
  ];

  readonly windowHours = signal<WindowHours>(this.readSavedWindow());
  readonly bucketMinutes = computed<number>(() => {
    const w = this.windowHours();
    if (w === 1) return 5;
    if (w === 6) return 15;
    return 60;
  });

  readonly timeline = signal<TokenTimeline | null>(null);
  readonly loading = signal(false);
  readonly hoverCell = signal<TokenTimelineCell | null>(null);
  readonly disabledProjects = signal<Set<string>>(this.readSavedDisabled());

  private pollTimer: VisibleIntervalHandle | null = null;

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setVisibleInterval(() => this.refresh(), 10_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  setWindow(h: WindowHours): void {
    if (this.windowHours() === h) return;
    this.windowHours.set(h);
    try { localStorage.setItem(STORAGE_WINDOW_KEY, String(h)); } catch { /* ignore */ }
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.tokensApi.getWorkspaceTokensTimeline(this.windowHours(), this.bucketMinutes())
      .subscribe({
        next: (t) => { this.timeline.set(t); this.loading.set(false); },
        error: () => { this.loading.set(false); /* keep last value */ },
      });
  }

  // ---- Project filter ----

  isProjectDisabled(project: string): boolean {
    return this.disabledProjects().has(project);
  }

  toggleProject(project: string): void {
    const next = new Set(this.disabledProjects());
    if (next.has(project)) next.delete(project); else next.add(project);
    this.disabledProjects.set(next);
    try { localStorage.setItem(STORAGE_DISABLED_KEY, JSON.stringify([...next])); } catch { /* ignore */ }
  }

  private readSavedDisabled(): Set<string> {
    try {
      const raw = localStorage.getItem(STORAGE_DISABLED_KEY);
      if (!raw) return new Set();
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) return new Set(parsed.filter(x => typeof x === 'string'));
    } catch { /* ignore */ }
    return new Set();
  }

  private readSavedWindow(): WindowHours {
    try {
      const raw = localStorage.getItem(STORAGE_WINDOW_KEY);
      if (raw) {
        const n = Number.parseInt(raw, 10);
        if (n === 1 || n === 6 || n === 24 || n === 168) return n as WindowHours;
      }
    } catch { /* ignore */ }
    return 24;
  }

  // ---- Derived chart data ----

  readonly hasAnyData = computed(() => {
    const t = this.timeline();
    if (!t) return false;
    const off = this.disabledProjects();
    return t.cells.some(c => !off.has(c.project) && c.total > 0);
  });

  readonly bars = computed<BucketBar[]>(() => {
    const t = this.timeline();
    if (!t) return [];
    const start = Date.parse(t.windowStart);
    const end = Date.parse(t.windowEnd);
    const span = Math.max(1, end - start);
    const bucketMs = t.bucketMinutes * 60 * 1000;
    const widthPct = (bucketMs / span) * 100;

    // Bucket key (ISO start) -> bar accumulator. Filter disabled projects.
    const off = this.disabledProjects();
    const byBucket = new Map<string, { startMs: number; endIso: string; cells: TokenTimelineCell[] }>();
    for (const c of t.cells) {
      if (off.has(c.project)) continue;
      const key = c.bucketStart;
      let acc = byBucket.get(key);
      if (!acc) {
        acc = { startMs: Date.parse(c.bucketStart), endIso: c.bucketEnd, cells: [] };
        byBucket.set(key, acc);
      }
      acc.cells.push(c);
    }

    // Find max bucket total for y-axis scale.
    let maxTotal = 0;
    for (const acc of byBucket.values()) {
      let s = 0;
      for (const c of acc.cells) s += c.total;
      if (s > maxTotal) maxTotal = s;
    }
    if (maxTotal <= 0) return [];

    // Project order (for stable stacking) — sort by total descending using the
    // server-supplied per-project totals, projects not in cells go last.
    const order = new Map<string, number>();
    t.projects.forEach((p, i) => order.set(p.project, i));

    const bars: BucketBar[] = [];
    for (const [key, acc] of byBucket) {
      acc.cells.sort((a, b) => (order.get(a.project) ?? 999) - (order.get(b.project) ?? 999));
      let yAcc = 0;
      const total = acc.cells.reduce((s, c) => s + c.total, 0);
      const segments: BucketSegment[] = acc.cells.map(c => {
        const hPct = (c.total / maxTotal) * 100;
        const seg: BucketSegment = {
          project: c.project,
          cell: c,
          total: c.total,
          yPct: yAcc,
          hPct,
          color: this.colorFor(c.project),
        };
        yAcc += hPct;
        return seg;
      });
      const xPct = ((acc.startMs - start) / span) * 100;
      bars.push({
        bucketStart: key,
        bucketEnd: acc.endIso,
        total,
        segments,
        xPct,
        widthPct,
      });
    }
    bars.sort((a, b) => a.bucketStart.localeCompare(b.bucketStart));
    return bars;
  });

  readonly gridLines = computed<{ pct: number; label: string }[]>(() => {
    const t = this.timeline();
    if (!t) return [];
    const off = this.disabledProjects();
    const byBucket = new Map<string, number>();
    for (const c of t.cells) {
      if (off.has(c.project)) continue;
      byBucket.set(c.bucketStart, (byBucket.get(c.bucketStart) ?? 0) + c.total);
    }
    let maxTotal = 0;
    for (const v of byBucket.values()) if (v > maxTotal) maxTotal = v;
    if (maxTotal <= 0) return [];
    return [0, 25, 50, 75, 100].map(pct => ({
      pct,
      label: this.formatTokens(Math.round((pct / 100) * maxTotal)),
    }));
  });

  readonly xTicks = computed<{ pct: number; label: string; iso: string }[]>(() => {
    const t = this.timeline();
    if (!t) return [];
    const start = Date.parse(t.windowStart);
    const end = Date.parse(t.windowEnd);
    const ticks: { pct: number; label: string; iso: string }[] = [];
    const w = this.windowHours();
    const count = w === 1 ? 4 : w === 6 ? 6 : w === 24 ? 6 : 7;
    for (let i = 0; i <= count; i++) {
      const ms = start + ((end - start) * i) / count;
      const d = new Date(ms);
      let label: string;
      if (w >= 168) {
        label = `${d.getMonth() + 1}/${d.getDate()}`;
      } else if (w >= 24) {
        label = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
      } else {
        label = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
      }
      ticks.push({ pct: (i / count) * 100, label, iso: d.toISOString() });
    }
    return ticks;
  });

  readonly totalCalls = computed(() => {
    const t = this.timeline();
    if (!t) return 0;
    const off = this.disabledProjects();
    let s = 0;
    for (const p of t.projects) if (!off.has(p.project)) s += p.calls;
    return s;
  });

  readonly grandTotalTokens = computed(() => {
    const t = this.timeline();
    if (!t) return 0;
    const off = this.disabledProjects();
    let s = 0;
    for (const p of t.projects) if (!off.has(p.project)) s += p.total;
    return s;
  });

  // Category descriptors for the breakdown chips + table columns. The three
  // shares add up to the grand total, so chips, table footer, and chart all
  // reconcile (Summen-invariante, AGT-2017 house rule).
  readonly categoryMeta = [
    { key: 'agent',        label: 'Agent',        cls: 'wtt__cat--agent' },
    { key: 'supporting',   label: 'Supporting',   cls: 'wtt__cat--supporting' },
    { key: 'orchestrator', label: 'Orchestrator', cls: 'wtt__cat--orchestrator' },
  ] as const;

  // Workspace-wide category split across the enabled projects.
  readonly categoryTotals = computed<{ agent: number; supporting: number; orchestrator: number }>(() => {
    const t = this.timeline();
    const off = this.disabledProjects();
    let agent = 0, supporting = 0, orchestrator = 0;
    if (t) {
      for (const p of t.projects) {
        if (off.has(p.project)) continue;
        agent += p.agentTokens;
        supporting += p.supportingTokens;
        orchestrator += p.orchestratorTokens;
      }
    }
    return { agent, supporting, orchestrator };
  });

  // Column totals for the table footer, folded over the enabled projects.
  // This row is the invariant anchor: its Total equals the header grand
  // total and the sum of the category chips.
  readonly tableTotals = computed(() => {
    const t = this.timeline();
    const off = this.disabledProjects();
    let total = 0, agent = 0, supporting = 0, orchestrator = 0, calls = 0, dollars = 0;
    let anyDollars = false, allPriced = true, anyActive = false;
    if (t) {
      for (const p of t.projects) {
        if (off.has(p.project)) continue;
        anyActive = true;
        total += p.total;
        agent += p.agentTokens;
        supporting += p.supportingTokens;
        orchestrator += p.orchestratorTokens;
        calls += p.calls;
        if (p.dollars !== null) {
          dollars += p.dollars;
          anyDollars = true;
          if (!p.allModelsPriced) allPriced = false;
        } else {
          allPriced = false;
        }
      }
    }
    return { total, agent, supporting, orchestrator, calls, dollars: anyDollars ? dollars : null, allPriced, anyActive };
  });

  // ---- Layout helpers (viewBox math) ----

  yFromPct(pct: number): number {
    const usable = this.svgH - this.padT - this.padB;
    return this.padT + usable - (pct / 100) * usable;
  }
  xFromPct(pct: number): number {
    const usable = this.svgW - this.padL - this.padR;
    return this.padL + (pct / 100) * usable;
  }
  widthFromPct(pct: number): number {
    const usable = this.svgW - this.padL - this.padR;
    return Math.max(1, (pct / 100) * usable - 1);
  }
  heightFromPct(pct: number): number {
    const usable = this.svgH - this.padT - this.padB;
    return Math.max(0, (pct / 100) * usable);
  }

  // ---- Colour palette: stable per project name ----

  colorFor(project: string): string {
    // Hash the name to a hue; saturation/lightness fixed so the dark
    // theme stays coherent. Same algorithm runs on every load so a
    // project always gets the same band colour.
    let h = 0;
    for (let i = 0; i < project.length; i++) {
      h = (h * 31 + project.charCodeAt(i)) >>> 0;
    }
    const hue = h % 360;
    return `hsl(${hue}, 65%, 55%)`;
  }

  /** Share of the enabled grand total, for the composition bar widths. */
  pct(n: number): number {
    const total = this.grandTotalTokens();
    if (total <= 0) return 0;
    return (n / total) * 100;
  }

  // ---- Formatting ----

  readonly formatTokens = formatUsageTokens;
  readonly formatUsd = formatUsageCurrency;

  formatBucketRange(c: TokenTimelineCell): string {
    const a = new Date(c.bucketStart);
    const b = new Date(c.bucketEnd);
    return `${pad2(a.getHours())}:${pad2(a.getMinutes())} – ${pad2(b.getHours())}:${pad2(b.getMinutes())}`;
  }

  formatTime(iso: string): string {
    const d = new Date(iso);
    return `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  }

  formatAgo(iso: string): string {
    const ms = Date.now() - Date.parse(iso);
    if (!Number.isFinite(ms)) return 'never';
    const sec = Math.floor(ms / 1000);
    if (sec < 60) return `${sec}s ago`;
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min}m ago`;
    const hr = Math.floor(min / 60);
    if (hr < 24) return `${hr}h ago`;
    const d = Math.floor(hr / 24);
    return `${d}d ago`;
  }
}

function pad2(n: number): string {
  return n < 10 ? '0' + n : String(n);
}
