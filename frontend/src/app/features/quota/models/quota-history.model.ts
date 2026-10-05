/**
 * Wire types of `GET /api/cli/quota/history?cli=<cli>&hours=<n>` (AGT-3001).
 * The backend records every trusted quota snapshot per CLI and window and
 * computes the three-hour burn rate and the 100 % forecast; the UI only renders.
 */
export type QuotaWindowKind = 'session' | 'weekly' | 'other';

export type QuotaForecastStatus =
  | 'no-data'
  | 'insufficient-data'
  | 'reached'
  | 'idle'
  | 'full-before-reset'
  | 'resets-first';

export interface QuotaHistoryPoint {
  at: string;
  usedPct: number;
  resetAt: string | null;
}

export interface QuotaForecast {
  status: QuotaForecastStatus;
  currentPct: number | null;
  currentAt: string | null;
  ratePctPerHour: number | null;
  rateFrom: string | null;
  forecastFullAt: string | null;
  resetAt: string | null;
  reachesFullBeforeReset: boolean;
}

export interface QuotaHistoryWindow {
  label: string;
  kind: QuotaWindowKind;
  points: QuotaHistoryPoint[];
  forecast: QuotaForecast;
}

export interface QuotaHistoryResponse {
  cliType: string;
  hours: number;
  from: string;
  to: string;
  retentionDays: number;
  rateLookbackHours: number;
  windows: QuotaHistoryWindow[];
}
