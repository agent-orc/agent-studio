/**
 * Human Review acceptance readiness feature public API (AGT-3564). The eager
 * lane loads only `AcceptanceReadinessLaneGrouping`; the store, models, and
 * lane components live in `lazy.ts` and load behind `@defer`.
 */
export { AcceptanceReadinessLaneGrouping } from './state/acceptance-readiness-lane-grouping';
