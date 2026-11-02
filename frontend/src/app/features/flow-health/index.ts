/** Project flow-health feature public API (ADR-0034 barrel). */
export { FlowHealthPageComponent, type FlowDetailTarget } from './components/flow-health-page/flow-health-page';
export { FLOW_FIGURE_TARGETS, FLOW_STAGE_WAIT_TARGET_MINUTES } from './models/flow-health-targets';
export { deriveFlowHealth, joinFlowSources } from './state/flow-health.derive';
export type { FlowHealthView, FlowBrake } from './models/flow-health.model';
