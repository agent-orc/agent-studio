export interface EnvironmentFlowSnapshot {
  capturedAt: string;
  today: {
    created: number; delivered: number; reviewed: number; integrated: number;
    deployed: number | null; completed: number; readyToIntegrationMedianMinutes: number | null;
  };
  lanes: readonly { lane: string; entered: number; left: number; medianMinutes: number | null }[];
  queues: readonly { queue: string; waiting: number | null; oldestWaitMinutes: number | null;
    throughputPerHour: number | null }[];
  waitingReasons: readonly { reason: string; count: number }[];
}

export function minutes(value: number | null | undefined): string {
  if (value == null) return 'Unavailable';
  return value < 60 ? `${Math.round(value)} min` : `${(value / 60).toFixed(1)} h`;
}
