import type { CliModelRoutesResponse } from './services/quota-api.service';
import type { QuotaForecast, QuotaHistoryWindow } from './models/quota-history.model';

/**
 * Pure helpers behind the quota forecast panel (AGT-3001): the fallback
 * "armed" decision, the curve geometry, and the forecast wording. The rate and
 * the forecast itself are computed by the backend; nothing here re-derives them.
 */

export interface QuotaFallbackStatus {
  /** "codex · gpt-5.6-sol (high)"; null when no fallback route exists. */
  target: string | null;
  /** True when the runner would switch to the fallback at the cap right now. */
  armed: boolean;
  /** True when the CLI already runs on its fallback (cap crossed or operator preference). */
  engaged: boolean;
  /** True when the route comes from the equivalence catalogue, not an operator override. */
  derived: boolean;
  /** Cap percent of the named window at which the switch happens, when known. */
  capPct: number | null;
  reason: string;
}

/**
 * Whether the configured fallback of `cliType` would take over when the cap is
 * reached. Mirrors `CliQuotaFallbackService.Resolve`: a route needs a fallback
 * model, and a cross-CLI fallback whose own cap is already crossed is refused.
 */
export function quotaFallbackStatus(
  routes: CliModelRoutesResponse | null,
  cliType: string,
  windowLabel: string | null = null,
): QuotaFallbackStatus {
  const none = { target: null, armed: false, engaged: false, derived: false, capPct: null };
  if (!routes) return { ...none, reason: 'Fallback routes are unavailable.' };

  const own = routes.states?.[cliType];
  const capPct = own?.windows.find(w => w.label === windowLabel)?.capPct ?? null;
  const profile = routes.profiles?.[cliType];
  if (!profile?.fallbackModel) {
    return { ...none, capPct, reason: 'No fallback route is configured.' };
  }

  const fallbackCli = profile.fallbackCliType || cliType;
  const thinking = profile.fallbackThinkingLevel ? ` (${profile.fallbackThinkingLevel})` : '';
  const target = `${fallbackCli} · ${profile.fallbackModel}${thinking}`;
  const derived = profile.isFallbackDerived === true;
  const engaged = own != null && own.state !== 'normal';
  const fallbackBlocked = fallbackCli !== cliType
    && routes.states?.[fallbackCli]?.state === 'fallback-active';

  if (fallbackBlocked) {
    return { target, armed: false, engaged, derived, capPct, reason: `${fallbackCli} is at its own usage cap.` };
  }
  const reason = engaged
    ? 'The fallback is engaged now.'
    : capPct != null
      ? `Switches when this window reaches its ${capPct}% cap.`
      : 'Switches when the usage cap is reached.';
  return { target, armed: true, engaged, derived, capPct, reason };
}

export interface CurvePoint {
  x: number;
  y: number;
  at: string;
  usedPct: number;
}

export interface CurveGeometry {
  width: number;
  height: number;
  plotLeft: number;
  plotRight: number;
  plotTop: number;
  plotBottom: number;
  points: CurvePoint[];
  /** SVG path of the recorded series; split into segments at gaps and resets. */
  path: string;
  /** Readings with no neighbour in their segment; drawn as dots because a lone path point is invisible. */
  isolated: CurvePoint[];
  /** Dashed extrapolation from the latest reading toward 100%, clipped at the reset and the plot edge. */
  forecast: { x1: number; y1: number; x2: number; y2: number } | null;
  nowX: number;
  resetX: number | null;
  gridY: { pct: number; y: number }[];
  /** Tick marks every 12 hours, aligned to the start of the range. */
  ticksX: { x: number; label: string }[];
}

export const CURVE_WIDTH = 720;
export const CURVE_HEIGHT = 150;
const PLOT_LEFT = 36;
const PLOT_RIGHT = CURVE_WIDTH - 12;
const PLOT_TOP = 10;
const PLOT_BOTTOM = CURVE_HEIGHT - 22;
/** Readings further apart than this are not joined by a line. */
const GAP_MS = 4 * 3_600_000;

/**
 * Geometry of the weekly curve. X spans `from` to `to + lookaheadHours` so the
 * forecast has room to the right of "now"; Y is a fixed 0 to 100% scale so
 * curves of different CLIs compare at a glance.
 */
export function quotaCurveGeometry(
  window: QuotaHistoryWindow,
  fromIso: string,
  toIso: string,
  lookaheadHours = 12,
  tickLabel: (at: Date) => string = at => `${String(at.getHours()).padStart(2, '0')}:00`,
): CurveGeometry {
  const from = Date.parse(fromIso);
  const now = Date.parse(toIso);
  const end = now + lookaheadHours * 3_600_000;
  const x = (ms: number) => PLOT_LEFT + ((ms - from) / (end - from)) * (PLOT_RIGHT - PLOT_LEFT);
  const y = (pct: number) => PLOT_BOTTOM - (Math.max(0, Math.min(100, pct)) / 100) * (PLOT_BOTTOM - PLOT_TOP);
  const round = (n: number) => Math.round(n * 10) / 10;

  const points = window.points
    .map(p => ({ ms: Date.parse(p.at), p }))
    .filter(({ ms }) => ms >= from && ms <= end)
    .sort((a, b) => a.ms - b.ms)
    .map(({ ms, p }) => ({ ms, x: round(x(ms)), y: round(y(p.usedPct)), at: p.at, usedPct: p.usedPct }));

  const breaksBefore = points.map((p, i) => {
    const prev = points[i - 1];
    return !prev || p.ms - prev.ms > GAP_MS || prev.usedPct - p.usedPct > 1;
  });
  const path = points.map((p, i) => `${breaksBefore[i] ? 'M' : 'L'}${p.x} ${p.y}`).join(' ');
  const isolated = points
    .filter((_, i) => breaksBefore[i] && (i === points.length - 1 || breaksBefore[i + 1]))
    .map(({ x: px, y: py, at, usedPct }) => ({ x: px, y: py, at, usedPct }));

  const f = window.forecast;
  const resetMs = f.resetAt ? Date.parse(f.resetAt) : NaN;
  let forecast: CurveGeometry['forecast'] = null;
  const last = points[points.length - 1];
  if (last && f.ratePctPerHour && f.ratePctPerHour > 0 && f.currentAt && f.currentPct != null && f.forecastFullAt) {
    const startMs = Date.parse(f.currentAt);
    const fullMs = Date.parse(f.forecastFullAt);
    // Usage drops at the reset, so the projection ends there when it comes first.
    const stopMs = Math.min(fullMs, end, resetMs > startMs ? resetMs : Infinity);
    const stopPct = f.currentPct + ((stopMs - startMs) / 3_600_000) * f.ratePctPerHour;
    forecast = { x1: last.x, y1: last.y, x2: round(x(stopMs)), y2: round(y(stopPct)) };
  }

  const ticksX: CurveGeometry['ticksX'] = [];
  for (let t = from; t <= end; t += 12 * 3_600_000) ticksX.push({ x: round(x(t)), label: tickLabel(new Date(t)) });

  return {
    width: CURVE_WIDTH,
    height: CURVE_HEIGHT,
    plotLeft: PLOT_LEFT,
    plotRight: PLOT_RIGHT,
    plotTop: PLOT_TOP,
    plotBottom: PLOT_BOTTOM,
    points: points.map(({ x: px, y: py, at, usedPct }) => ({ x: px, y: py, at, usedPct })),
    path,
    isolated,
    forecast,
    nowX: round(x(now)),
    resetX: resetMs >= from && resetMs <= end ? round(x(resetMs)) : null,
    gridY: [0, 50, 100].map(pct => ({ pct, y: round(y(pct)) })),
    ticksX,
  };
}

/** Index of the reading closest to `plotX`, or -1 when there is none. */
export function nearestCurvePoint(points: CurvePoint[], plotX: number): number {
  let best = -1;
  let bestDistance = Infinity;
  points.forEach((p, i) => {
    const d = Math.abs(p.x - plotX);
    if (d < bestDistance) { best = i; bestDistance = d; }
  });
  return best;
}

/** "8h 30m" / "2d 4h" / "12m"; negative spans read as "now". */
export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return 'now';
  const minutes = Math.round(ms / 60_000);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return minutes % 60 === 0 ? `${hours}h` : `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return hours % 24 === 0 ? `${days}d` : `${days}d ${hours % 24}h`;
}

/** "2.0%/h"; an unknown rate reads "unknown". */
export function formatRate(rate: number | null): string {
  if (rate == null || !Number.isFinite(rate)) return 'unknown';
  return `${rate.toFixed(rate >= 10 ? 0 : 1)}%/h`;
}

/** Severity of a used percent for the session gauge and the curve badge. */
export function quotaTone(pct: number | null): 'ok' | 'warn' | 'critical' {
  if (pct == null) return 'ok';
  if (pct >= 90) return 'critical';
  if (pct >= 70) return 'warn';
  return 'ok';
}

/** One-line forecast sentence for a window; `when` formats absolute times. */
export function forecastSentence(forecast: QuotaForecast, nowMs: number, when: (iso: string) => string): string {
  switch (forecast.status) {
    case 'no-data':
      return 'No readings recorded yet.';
    case 'insufficient-data':
      return 'Not enough readings in the last 3 hours for a rate.';
    case 'reached':
      return 'At 100% now.';
    case 'idle':
      return 'No growth over the last 3 hours; 100% is not in sight at this rate.';
    case 'full-before-reset':
    case 'resets-first': {
      const full = forecast.forecastFullAt!;
      const inText = formatDuration(Date.parse(full) - nowMs);
      return forecast.status === 'full-before-reset'
        ? `100% at ${when(full)} (in ${inText}), before the reset.`
        : `Would reach 100% at ${when(full)}; the reset comes first.`;
    }
  }
}
