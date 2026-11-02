import type { GateEconomicsResponse, GateEconomicsRow } from '../../task-pipeline/models/gate-economics.model';
import type { PipelineHealthSnapshot } from '../../task-pipeline/models/pipeline-health.model';
import type { ProjectCycleTimeResponse } from '../../project-cycle-time/models/project-cycle-time.model';
import type { BatchGateReport } from '../../../models/batch-gate.model';
import type { FlowFigureKey, FlowFigureUnit, FlowStageKey, FlowTarget } from './flow-health-targets';

export type FlowWindow = '7d' | '30d';
/** The gate-economics row that aggregates every visible project. */
export const ALL_PROJECTS_ROW = 'All projects';
export const FLOW_WINDOWS: readonly FlowWindow[] = ['7d', '30d'];
export const FLOW_WINDOW_DAYS: Readonly<Record<FlowWindow, number>> = { '7d': 7, '30d': 30 };

/** Busy and total slots of one execution plane; null fields are unknown. */
export interface FlowSlots {
  busy: number | null;
  total: number | null;
}

/** The existing read models one window of the page joins. Null: not loaded or unavailable. */
export interface FlowWindowSources {
  economics: GateEconomicsRow | null;
  economicsAll: GateEconomicsRow | null;
  cycleTime: ProjectCycleTimeResponse | null;
}

export interface FlowHealthSources {
  project: string;
  /** Wall clock of the derivation, epoch milliseconds. */
  nowMs: number;
  windows: Readonly<Record<FlowWindow, FlowWindowSources>>;
  pipelineHealth: PipelineHealthSnapshot | null;
  batchReport: BatchGateReport | null;
  /** Null when the batch gate settings could not be read. */
  batchingEnabled: boolean | null;
  gateSlots: FlowSlots | null;
  reviewSlots: FlowSlots | null;
  codingSlots: FlowSlots | null;
}

/** The raw responses the page loads, before they are joined per window. */
export interface FlowHealthResponses {
  economics: Readonly<Record<FlowWindow, GateEconomicsResponse | null>>;
  cycleTime: Readonly<Record<FlowWindow, ProjectCycleTimeResponse | null>>;
  pipelineHealth: PipelineHealthSnapshot | null;
  batchReport: BatchGateReport | null;
  batchingEnabled: boolean | null;
  gateSlots: FlowSlots | null;
  reviewSlots: FlowSlots | null;
  codingSlots: FlowSlots | null;
}

export type FlowStatus = 'met' | 'missed' | 'unknown' | 'not-applicable';
export type FlowTrend = 'better' | 'worse' | 'steady' | 'unknown';
export type FlowBlockKey = 'grouping' | 'gate' | 'review';

export interface FlowFigure {
  /** Stable row identity; aspect rows carry the aspect step id. */
  id: string;
  key: FlowFigureKey;
  label: string;
  unit: FlowFigureUnit;
  target: FlowTarget;
  value7d: number | null;
  value30d: number | null;
  /** The all-projects value over 7 days; undefined when the source is per project only. */
  all7d?: number | null;
  status: FlowStatus;
  trend: FlowTrend;
  /** Why a value is missing or not applicable. */
  note?: string;
}

export interface FlowBlock {
  key: FlowBlockKey;
  title: string;
  figures: FlowFigure[];
}

export interface FlowStageWait {
  stage: FlowStageKey;
  label: string;
  /** Lane key for the lane-tone dot; null for the integration gate. */
  lane: string | null;
  waiting: number;
  oldestMinutes: number | null;
  targetMinutes: number;
  slots: FlowSlots | null;
  reason: string;
  status: FlowStatus;
}

export interface FlowBrake {
  /** The stage holding the brake, or null for a figure brake, no brake, or no data. */
  stage: FlowStageKey | null;
  figure: FlowFigureKey | null;
  tone: 'brake' | 'calm' | 'empty';
  sentence: string;
}

export interface FlowHealthView {
  brake: FlowBrake;
  blocks: FlowBlock[];
  now: FlowStageWait[];
  /** Lifetime batch pilot context under the grouping block; null without a report. */
  batchLifetime: string | null;
}
