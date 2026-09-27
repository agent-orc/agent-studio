import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';

export interface ProjectGraphSize {
  files: number;
  lines: number;
}

export interface ProjectGraphTechnology {
  slug: string;
  label: string;
}

export interface ProjectGraphProject {
  id: string;
  key: string;
  shortCode: string;
  displayName: string;
  status: 'ready' | 'partial' | 'unavailable';
  repositoryLabel: string | null;
  sourceRevision: string | null;
  sourceState: 'clean' | 'dirty' | 'unavailable';
  solutions: string[];
  workflows: string[];
  technologies: ProjectGraphTechnology[];
  componentIds: string[];
  size: ProjectGraphSize;
  warnings: string[];
}

export interface ProjectGraphComponentModel {
  id: string;
  projectId: string;
  projectKey: string;
  name: string;
  kind: string;
  relativePath: string;
  technologies: ProjectGraphTechnology[];
  size: ProjectGraphSize;
}

export interface ProjectGraphDependency {
  fromComponentId: string;
  toComponentId: string | null;
  kind: 'project-reference' | 'package';
  resolution: 'resolved' | 'unresolved';
  targetHint: string | null;
  evidence: string;
}

export interface ProjectGraphSnapshot {
  schemaVersion: number;
  generatorVersion: string;
  snapshotId: string;
  previousSnapshotId: string | null;
  captureMode: 'explicit-api' | 'imported';
  capturedAtUtc: string;
  focusProjectId: string;
  focusProjectKey: string;
  projects: ProjectGraphProject[];
  components: ProjectGraphComponentModel[];
  dependencies: ProjectGraphDependency[];
}

interface GraphNode {
  component: ProjectGraphComponentModel;
  x: number;
  y: number;
  external: boolean;
  shortName: string;
  sourceLine: string;
  ariaLabel: string;
}

interface GraphEdge extends ProjectGraphDependency {
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

interface ComponentRelation {
  id: string;
  direction: 'outgoing' | 'incoming';
  label: string;
  evidence: string;
  resolution: 'resolved' | 'unresolved';
}

const NODE_WIDTH = 220;
const NODE_HEIGHT = 94;
const COLUMN_STEP = 256;
const ROW_STEP = 126;
const GRAPH_MARGIN = 28;
const MAX_GRAPH_COMPONENTS = 36;

@Component({
  selector: 'app-project-graph',
  standalone: true,
  imports: [LoadingSurfaceComponent],
  templateUrl: './project-graph.component.html',
  styleUrl: './project-graph.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectGraphComponent {
  private readonly http = inject(HttpClient);
  private requestVersion = 0;

  readonly projectName = input.required<string>();
  readonly view = signal<'graph' | 'list'>('graph');
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly snapshot = signal<ProjectGraphSnapshot | null>(null);

  readonly focusProject = computed(() => {
    const snapshot = this.snapshot();
    return snapshot?.projects.find(project => project.key === snapshot.focusProjectKey) ?? null;
  });

  readonly focusComponents = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot) return [];
    return snapshot.components
      .filter(component => component.projectKey === snapshot.focusProjectKey)
      .sort((left, right) => left.name.localeCompare(right.name));
  });

  readonly focusDependencies = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot) return [];
    const ids = new Set(this.focusComponents().map(component => component.id));
    return snapshot.dependencies.filter(edge =>
      ids.has(edge.fromComponentId) || (edge.toComponentId !== null && ids.has(edge.toComponentId)));
  });

  readonly unresolvedDependencyCount = computed(() =>
    this.focusDependencies().filter(edge => edge.resolution === 'unresolved').length);

  readonly resolvedDependencyCount = computed(() =>
    this.focusDependencies().filter(edge => edge.resolution === 'resolved').length);

  readonly graphComponents = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot) return [];
    const focus = this.focusComponents();
    const ids = new Set(focus.map(component => component.id));
    for (const edge of this.focusDependencies()) {
      ids.add(edge.fromComponentId);
      if (edge.toComponentId) ids.add(edge.toComponentId);
    }
    return snapshot.components
      .filter(component => ids.has(component.id))
      .sort((left, right) => {
        const focusOrder = Number(right.projectKey === snapshot.focusProjectKey) - Number(left.projectKey === snapshot.focusProjectKey);
        return focusOrder || left.projectKey.localeCompare(right.projectKey) || left.name.localeCompare(right.name);
      })
      .slice(0, MAX_GRAPH_COMPONENTS);
  });

  readonly graphTruncated = computed(() => {
    const focusAndNeighbours = new Set(this.focusComponents().map(component => component.id));
    for (const edge of this.focusDependencies()) {
      focusAndNeighbours.add(edge.fromComponentId);
      if (edge.toComponentId) focusAndNeighbours.add(edge.toComponentId);
    }
    return focusAndNeighbours.size > MAX_GRAPH_COMPONENTS;
  });

  readonly graphNodes = computed<GraphNode[]>(() => this.graphComponents().map((component, index) => {
    const columns = this.graphColumnCount();
    const project = this.snapshot()?.projects.find(candidate => candidate.id === component.projectId);
    const sourceLine = project
      ? `${project.sourceRevision?.slice(0, 8) ?? 'no revision'} · ${project.sourceState}`
      : 'source unavailable';
    return {
      component,
      x: GRAPH_MARGIN + (index % columns) * COLUMN_STEP,
      y: GRAPH_MARGIN + Math.floor(index / columns) * ROW_STEP,
      external: component.projectKey !== this.snapshot()?.focusProjectKey,
      shortName: component.name.length > 26 ? `${component.name.slice(0, 23)}…` : component.name,
      sourceLine,
      ariaLabel: `${component.projectKey} ${component.name}, ${component.kind}, ${sourceLine}, ${this.componentRelations(component.id).length} relations`,
    };
  }));

  readonly graphEdges = computed<GraphEdge[]>(() => {
    const positions = new Map(this.graphNodes().map(node => [node.component.id, node]));
    return this.focusDependencies().flatMap(edge => {
      const from = positions.get(edge.fromComponentId);
      const to = edge.toComponentId ? positions.get(edge.toComponentId) : undefined;
      if (!from || !to) return [];
      const fromCenter = { x: from.x + NODE_WIDTH / 2, y: from.y + NODE_HEIGHT / 2 };
      const toCenter = { x: to.x + NODE_WIDTH / 2, y: to.y + NODE_HEIGHT / 2 };
      const horizontal = Math.abs(toCenter.x - fromCenter.x) >= Math.abs(toCenter.y - fromCenter.y);
      return [{
        ...edge,
        x1: horizontal ? from.x + (toCenter.x > fromCenter.x ? NODE_WIDTH : 0) : fromCenter.x,
        y1: horizontal ? fromCenter.y : from.y + (toCenter.y > fromCenter.y ? NODE_HEIGHT : 0),
        x2: horizontal ? to.x + (toCenter.x > fromCenter.x ? 0 : NODE_WIDTH) : toCenter.x,
        y2: horizontal ? toCenter.y : to.y + (toCenter.y > fromCenter.y ? 0 : NODE_HEIGHT),
      }];
    });
  });

  readonly graphWidth = computed(() => GRAPH_MARGIN * 2 + this.graphColumnCount() * COLUMN_STEP - (COLUMN_STEP - NODE_WIDTH));
  readonly graphHeight = computed(() => {
    const rows = Math.max(1, Math.ceil(this.graphComponents().length / this.graphColumnCount()));
    return GRAPH_MARGIN * 2 + rows * ROW_STEP - (ROW_STEP - NODE_HEIGHT);
  });

  readonly crossProjectDependencyCount = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot) return 0;
    const byId = new Map(snapshot.components.map(component => [component.id, component.projectKey]));
    return this.focusDependencies().filter(edge =>
      edge.resolution === 'resolved'
      && edge.toComponentId !== null
      && byId.get(edge.fromComponentId) !== byId.get(edge.toComponentId)).length;
  });

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  setView(view: 'graph' | 'list'): void {
    this.view.set(view);
  }

  formatCount(value: number): string {
    return new Intl.NumberFormat('en-US', { notation: value >= 10_000 ? 'compact' : 'standard', maximumFractionDigits: 1 }).format(value);
  }

  formatCapturedAt(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString('en-US');
  }

  componentRelations(componentId: string): ComponentRelation[] {
    const snapshot = this.snapshot();
    if (!snapshot) return [];
    const byId = new Map(snapshot.components.map(component => [component.id, component]));
    return snapshot.dependencies.flatMap((edge, index): ComponentRelation[] => {
      if (edge.fromComponentId === componentId) {
        const target = edge.toComponentId ? byId.get(edge.toComponentId) : null;
        return [{
          id: `${index}:out`,
          direction: 'outgoing' as const,
          label: target ? `${target.projectKey} / ${target.name}` : edge.targetHint ?? 'unresolved local target',
          evidence: edge.evidence,
          resolution: edge.resolution,
        }];
      }
      if (edge.toComponentId === componentId) {
        const source = byId.get(edge.fromComponentId);
        return [{
          id: `${index}:in`,
          direction: 'incoming' as const,
          label: source ? `${source.projectKey} / ${source.name}` : 'unknown source',
          evidence: edge.evidence,
          resolution: edge.resolution,
        }];
      }
      return [];
    });
  }

  private graphColumnCount(): number {
    return Math.min(3, Math.max(1, this.graphComponents().length));
  }

  private load(projectName: string): void {
    const version = ++this.requestVersion;
    this.loading.set(true);
    this.error.set(null);
    this.snapshot.set(null);
    this.http.get<ProjectGraphSnapshot>(`/api/projects/${encodeURIComponent(projectName)}/graph`).subscribe({
      next: snapshot => {
        if (version !== this.requestVersion) return;
        this.snapshot.set(snapshot);
        this.loading.set(false);
      },
      error: () => {
        if (version !== this.requestVersion) return;
        this.error.set('No persisted Project Graph capture is available. Run the documented explicit capture command first.');
        this.loading.set(false);
      },
    });
  }
}
