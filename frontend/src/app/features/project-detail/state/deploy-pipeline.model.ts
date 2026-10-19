/**
 * The project.yml pipeline as the Deploy area draws it. Two sources share one
 * view: AGT-3221's `GET /api/projects/{p}/pipeline-definition` diagram when the
 * server has that route, otherwise the `lifecycle` projection of
 * `GET /api/projects/{p}/execution`. Both keep the parsed diagram of an
 * invalid file next to its validation issues.
 */

export interface DefinitionIssueRead {
  path: string;
  code: string;
  message: string;
  line?: number | null;
}

/** AGT-3221 `PipelineDefinitionResponse` (PipelineDiagram.cs). */
export interface PipelineDefinitionRead {
  valid: boolean;
  issues: DefinitionIssueRead[];
  diagram: {
    gate: { source: string; build: string[]; test: string[]; testSuites: string[] };
    artifacts: { name: string; type: string; source: string | null; image: string | null }[];
    environments: {
      name: string;
      order: number;
      protected: boolean;
      automatic: boolean;
      approval: string[];
      requires: string | null;
      steps: {
        kind: string;
        artifact: string | null;
        target: string | null;
        destination: string | null;
        healthUrl: string | null;
        healthTimeoutSeconds: number | null;
      }[];
    }[];
  } | null;
}

/** This server's `ProjectDeployLifecycleView`, served as `lifecycle` with the execution definition. */
export interface ExecutionLifecycleRead {
  valid: boolean;
  issues?: DefinitionIssueRead[];
  lifecycle?: {
    declared: boolean;
    nodes: {
      id: string;
      kind: 'gate' | 'artifact' | 'environment';
      name: string;
      items: string[];
      steps: { kind: string; artifact: string | null; target: string | null; destination: string | null; health: string | null }[];
      automatic: boolean;
      approval: string[];
      requires: string | null;
    }[];
  } | null;
}

export interface PipelineStepView {
  kind: string;
  artifact: string | null;
  target: string | null;
  destination: string | null;
  health: string | null;
  healthTimeoutSeconds: number | null;
}

export interface PipelineNodeView {
  id: string;
  kind: 'gate' | 'artifact' | 'environment';
  name: string;
  items: string[];
  steps: PipelineStepView[];
  automatic: boolean;
  approval: string[];
  requires: string | null;
}

export interface DeployPipelineView {
  valid: boolean;
  issues: { path: string; message: string; line: number | null }[];
  /** Null when no diagram could be parsed; empty when the file declares no deployment. */
  nodes: PipelineNodeView[] | null;
}

const STEP_KIND_LABEL: Record<string, string> = {
  'static-site': 'Static site',
  container: 'Container',
  'publish-static': 'Publish static',
  'start-container': 'Start container',
  run: 'Run',
  health: 'Health check',
  approval: 'Approval',
};

/** A step kind this build does not know keeps its project.yml name. */
export function stepKindLabel(kind: string): string {
  return STEP_KIND_LABEL[kind] ?? kind;
}

function issues(read: DefinitionIssueRead[] | undefined): DeployPipelineView['issues'] {
  return (read ?? []).map(issue => ({ path: issue.path, message: issue.message, line: issue.line ?? null }));
}

export function pipelineFromDefinition(read: PipelineDefinitionRead): DeployPipelineView {
  const diagram = read.diagram;
  if (!diagram) return { valid: read.valid, issues: issues(read.issues), nodes: null };
  if (diagram.environments.length === 0) return { valid: read.valid, issues: issues(read.issues), nodes: [] };
  const environments = [...diagram.environments].sort((a, b) => a.order - b.order);
  const nodes: PipelineNodeView[] = [
    { id: 'gate', kind: 'gate', name: 'Gate', items: [...diagram.gate.build, ...diagram.gate.test], steps: [],
      automatic: true, approval: [], requires: null },
    { id: 'artifact', kind: 'artifact', name: 'Artifact', steps: [], automatic: true, approval: [], requires: 'gate',
      items: diagram.artifacts.map(item => {
        const detail = [item.type, item.source, item.image].filter(Boolean).join(', ');
        return `${item.name} (${detail})`;
      }) },
    ...environments.map((environment): PipelineNodeView => ({
      id: environment.name, kind: 'environment', name: environment.name, items: [],
      automatic: environment.automatic, approval: environment.approval, requires: environment.requires ?? 'artifact',
      steps: environment.steps.map(step => ({
        kind: step.kind, artifact: step.artifact, target: step.target, destination: step.destination,
        health: step.healthUrl, healthTimeoutSeconds: step.healthTimeoutSeconds,
      })),
    })),
  ];
  return { valid: read.valid, issues: issues(read.issues), nodes };
}

export function pipelineFromExecution(read: ExecutionLifecycleRead): DeployPipelineView {
  const lifecycle = read.lifecycle;
  if (!lifecycle) return { valid: read.valid, issues: issues(read.issues), nodes: null };
  const nodes = lifecycle.declared
    ? lifecycle.nodes.map((node): PipelineNodeView => ({
      ...node, steps: node.steps.map(step => ({ ...step, healthTimeoutSeconds: null })),
    }))
    : [];
  return { valid: read.valid, issues: issues(read.issues), nodes };
}
