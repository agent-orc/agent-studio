/** One late tick of the API stall detector (AGT-3369). */
export interface ApiStall {
  detectedAtUtc: string;
  delayMs: number;
  gcPauseMs: number;
  threadPoolThreads: number;
  pendingWorkItems: number;
  processThreads: number;
}

/** `GET /api/diagnostics/api-stalls`. */
export interface ApiStallSnapshot {
  status: 'ok' | 'warning' | 'alarm';
  checkedAtUtc: string;
  intervalMs: number;
  warningThresholdMs: number;
  alarmThresholdMs: number;
  alarmWindowMinutes: number;
  maxDelayMs: number;
  recent: ApiStall[];
}
