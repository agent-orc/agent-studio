/**
 * Per-step and per-kind "what happens here" copy plus the pipeline phase
 * catalogue, split out of `overview-pane.component.ts` in AGT-2819. Reference
 * data, so it lives beside the component rather than inside it.
 */
import type { StructuredTooltip } from 'coding-agent-chat/shared';
import type { StepKind } from '../../../../task-pipeline';
import type { PipelinePhaseKey, PipelinePhaseVm } from './pipeline-row.vm';

/**
 * Per-step "what happens here" copy, keyed by the stable catalogue step id
 * (see backend PipelineCatalogue). Surfaced as the hover tooltip on every
 * pipeline-step name so the operator can learn what each pre / core / aspect /
 * tool / decision / drift step actually does without leaving the Overview.
 */
const PIPELINE_STEP_EXPLANATIONS: Record<string, string> = {
  'pre-loop-guard':
    'Auto-mode loop guard. Before the agent runs, a deterministic check makes sure the same task is not being re-issued in circles: it flags a forming loop while still under budget and trips the circuit-breaker once the iteration or token limit is hit, pausing for the user.',
  'pre-orchestrator-prep':
    'Opt-in prompt-readiness pass. Scores the task prompt for clarity while it is still in Preparation and either admits it to Ready or bounces it back for refinement. Runs off the coding seat, so it never blocks throughput.',
  'pre-model-qualification':
    'Zero-token model qualification. Classifies task type, size, affected surface, and similar project history, then maps that profile onto the selected CLI\'s live model and reasoning ladders. A model or level pinned on the card always wins; the recommendation remains visible for comparison.',
  'pre-reissue-open-items':
    'Re-issue guard. On a re-issued run it detects open items left from the previous attempt (the auto-review follow-up reason, unchecked checklist boxes, aspect concerns) and foregrounds them into the run prompt so the agent finishes them instead of starting over.',
  'core-agent-run':
    'The actual CLI coding run. The agent works the task in the repository until it reports done, blocks, or asks for input. This is the single sequential coding seat; every pre- and post-step wraps around it.',
  'aspect-requirement-fit':
    'Parallel review aspect. Checks whether the work matches the prompt\'s acceptance criteria and whether anything landed that the prompt did not ask for.',
  'aspect-code-quality':
    'Parallel review aspect. Scans the diff and changed-file list for obvious regressions, dead code, missing tests, or type errors.',
  'aspect-documentation-impact':
    'Parallel review aspect. Checks whether the change needs documentation updates (AGENTS.md, ROADMAP, ADRs, cli-skills, docs) and whether they were made.',
  'aspect-tests-and-evidence':
    'Parallel review aspect. Checks whether the agent shipped tests that fail before and pass after the change, and whether screenshot or log evidence is present where the contract requires it.',
  'post-git-commit-attribution':
    'Determines which git commits belong to this task by matching commit author-dates against the run\'s wall-clock windows. The work runs on the lane transition ahead of this bracket, so the row shows as planned here.',
  'post-lint-scss':
    'Runs stylelint over the frontend SCSS tree after the run. Depending on the configured gate mode (off, warn, or fail) a failure can trigger a re-issue back to Ready.',
  'post-regression-radar':
    'Deterministic spec-change analysis. Reads the task\'s attributed commits and classifies each changed spec as intended, at-risk, or drift. Reporting only: it never triggers a re-issue.',
  'post-orchestrator-review':
    'Post-core completeness check. Right after the agent reports done, a deterministic scan reads the run\'s own close-out evidence (open items, notes, the result line, and the log tail) for unfinished-work signals such as open checklist boxes or self-reported build / test failures. A hit re-issues the task with those items foregrounded before any review pass runs, so a task is never accepted while its own evidence says it is unfinished.',
  'post-orchestrator-decision':
    'The orchestrator\'s single final ruling. Aggregates the parallel aspect verdicts and decides re-issue, accept-as-done, or escalate. This is the step that moves the task out of auto-review.',
  'post-drift-adr-code':
    'Opt-in drift check (off by default). An LLM pass that looks for drift between the code and the decisions recorded in the ADRs.',
  'post-drift-software-architecture':
    'Opt-in drift check (off by default). An LLM pass that compares the code against the documented software-architecture intent.',
  'post-drift-docs-marketing':
    'Opt-in drift check (off by default). An LLM pass that checks whether docs and marketing copy still match what the software does.',
  'post-drift-spec-task-job':
    'Opt-in drift check (off by default). An LLM pass that checks whether specs, tasks, and jobs still agree with the implementation.',
  'post-drift-code-pattern':
    'Opt-in drift check (off by default). A rule-based scan for code-pattern drift, optionally enriched by an LLM verdict.',
  'post-abort-review':
    'Abort-triggered review (off by default). Runs only after a non-clean run end such as a watchdog timeout, non-zero exit, or unexpected stop: it reads the abort evidence and recommends rerun, a stronger re-issue, accept, or human review.',
};

/** Per-kind fallback copy for a step id not in the explicit catalogue map. */
const PIPELINE_KIND_EXPLANATIONS: Record<StepKind, string> = {
  module:       'A deterministic pre-processing step that runs before the agent.',
  core:         'The core CLI agent run for this task.',
  aspect:       'A read-only review aspect that runs in parallel after the agent finishes.',
  orchestrator: 'An orchestrator decision step that aggregates verdicts and chooses the next move.',
  tool:         'A deterministic tooling step that runs after the agent finishes.',
  analysis:     'A named Quality Studio analysis that returns canonical findings and provenance in process.',
  drift:        'An opt-in drift-analysis pass that runs after auto-review.',
};

/**
 * Catalogue id of the single FINAL orchestrator ruling. Only this row earns
 * the "Final verdict" chip / divider; the post-core `post-orchestrator-review`
 * early gate shares the `orchestrator` kind but is NOT the final verdict.
 * Mirrors backend `PipelineCatalogue.OrchestratorDecisionStepId`.
 */
export const FINAL_VERDICT_STEP_ID = 'post-orchestrator-decision';

export const PIPELINE_PHASES: Record<PipelinePhaseKey, PipelinePhaseVm> = {
  pre: {
    key: 'pre',
    label: 'PRE STEPS',
    description: 'Preparation checks before the agent gets the task.',
  },
  core: {
    key: 'core',
    label: 'CORE AGENT WORK',
    description: 'The coding agent work.',
  },
  aspect: {
    key: 'aspect',
    label: 'ASPECT',
    description: 'Parallel review passes over the finished work.',
  },
  tool: {
    key: 'tool',
    label: 'TOOL',
    description: 'Deterministic post-run tooling and evidence steps.',
  },
  analysis: {
    key: 'analysis',
    label: 'ANALYSIS',
    description: 'Named Quality Studio quality analyses over the completed change.',
  },
  decision: {
    key: 'decision',
    label: 'DECISION',
    description: 'The orchestrator ruling that accepts, reissues, or escalates.',
  },
  drift: {
    key: 'drift',
    label: 'DRIFT',
    description: 'Optional drift-analysis passes.',
  },
};

export function pipelinePhaseForKind(kind: StepKind): PipelinePhaseVm {
  switch (kind) {
    case 'module':       return PIPELINE_PHASES.pre;
    case 'core':         return PIPELINE_PHASES.core;
    case 'aspect':       return PIPELINE_PHASES.aspect;
    case 'tool':         return PIPELINE_PHASES.tool;
    case 'analysis':     return PIPELINE_PHASES.analysis;
    case 'orchestrator': return PIPELINE_PHASES.decision;
    case 'drift':        return PIPELINE_PHASES.drift;
    default:             return PIPELINE_PHASES.tool;
  }
}

/**
 * Build the always-present step-name explanation tooltip: the step's display
 * label as the title and the "what happens here" copy as the body, keyed by
 * step id with a per-kind fallback so a new catalogue step still explains
 * itself rather than rendering with no tooltip.
 */
export function buildStepExplanation(stepId: string, label: string, kind: StepKind): StructuredTooltip {
  const body =
    PIPELINE_STEP_EXPLANATIONS[stepId.toLowerCase()] ??
    PIPELINE_KIND_EXPLANATIONS[kind] ??
    'A pipeline step.';
  return { title: label, body };
}
