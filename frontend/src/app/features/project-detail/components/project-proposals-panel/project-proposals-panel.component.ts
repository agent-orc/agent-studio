import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { finalize } from 'rxjs';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { TaskService } from '../../../../services/task.service';

export interface ProjectProposal {
  id: string;
  generation: string;
  finding: string;
  evidenceScreenshot: string;
  proposal: string;
  estimatedEffort: string;
  severity: 'critical' | 'medium' | 'low';
  status: 'proposed' | 'approved' | 'rejected' | 'spawned';
  spawnedTask: string | null;
  topic: string;
  categories: string[];
  source: string;
  rejectionReason: string | null;
  rejectionReasonRaw: string | null;
  relPath: string;
  updatedAt: string;
}

const SEVERITY_TONES: Record<ProjectProposal['severity'], StatusTone> = {
  critical: 'danger',
  medium: 'warning',
  low: 'neutral',
};

/** Only a spawned proposal is an outcome; proposed, approved and rejected stay calm. */
const STATUS_TONES: Record<ProjectProposal['status'], StatusTone> = {
  proposed: 'neutral',
  approved: 'neutral',
  rejected: 'neutral',
  spawned: 'success',
};

/** Tone of a proposal severity; an unknown severity is neutral. */
export function proposalSeverityTone(severity: string): StatusTone {
  return SEVERITY_TONES[severity as ProjectProposal['severity']] ?? 'neutral';
}

/** Tone of a proposal review status; an unknown status is neutral. */
export function proposalStatusTone(status: string): StatusTone {
  return STATUS_TONES[status as ProjectProposal['status']] ?? 'neutral';
}

@Component({
  selector: 'app-project-proposals-panel',
  standalone: true,
  imports: [PendingButtonDirective, TaskReferenceMicrocardComponent, StatusIndicatorComponent],
  templateUrl: './project-proposals-panel.component.html',
  styleUrl: './project-proposals-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectProposalsPanelComponent {
  readonly severityTone = proposalSeverityTone;
  readonly statusTone = proposalStatusTone;
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly items = signal<ProjectProposal[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly loading = signal(false);
  readonly deciding = signal(false);
  readonly generating = signal(false);
  readonly refining = signal(false);
  readonly error = signal<string | null>(null);
  readonly taskStatuses = signal<ReadonlyMap<string, TaskReferenceStatus>>(new Map());
  readonly loadedThumbnails = signal<ReadonlySet<string>>(new Set());
  readonly detailImageState = signal<'loading' | 'loaded' | 'error'>('loading');
  readonly generationFilter = signal('all');
  readonly statusFilter = signal('all');
  readonly generationFormOpen = signal(false);
  readonly generationTopic = signal('');
  readonly generationGuidance = signal('');
  readonly rejectionFormOpen = signal(false);
  readonly rejectionRaw = signal('');
  readonly rejectionRefined = signal('');
  readonly removalConfirmation = signal<'proposal' | 'older' | null>(null);
  readonly selected = computed(() => this.items().find(p => p.id === this.selectedId()) ?? null);
  readonly proposedCount = computed(() => this.items().filter(p => p.status === 'proposed').length);
  readonly generations = computed(() => new Set(this.items().map(p => p.generation)).size);
  readonly generationOptions = computed(() => [...new Set(this.items().map(p => p.generation))].sort().reverse());
  readonly filteredItems = computed(() => this.items().filter(item =>
    (this.generationFilter() === 'all' || item.generation === this.generationFilter()) &&
    (this.statusFilter() === 'all' || item.status === this.statusFilter())));

  constructor() {
    effect(() => {
      const project = this.projectName();
      if (project) this.load(project);
    });
  }

  select(id: string): void {
    if (id === this.selectedId()) return;
    this.detailImageState.set('loading');
    this.selectedId.set(id);
    this.rejectionFormOpen.set(false);
    this.removalConfirmation.set(null);
  }

  setGenerationFilter(event: Event): void { this.generationFilter.set((event.target as HTMLSelectElement).value); }
  setStatusFilter(event: Event): void { this.statusFilter.set((event.target as HTMLSelectElement).value); }
  setGenerationTopic(event: Event): void { this.generationTopic.set((event.target as HTMLInputElement).value); }
  setGenerationGuidance(event: Event): void { this.generationGuidance.set((event.target as HTMLTextAreaElement).value); }
  setRejectionRaw(event: Event): void { this.rejectionRaw.set((event.target as HTMLTextAreaElement).value); this.rejectionRefined.set(''); }
  setRejectionRefined(event: Event): void { this.rejectionRefined.set((event.target as HTMLTextAreaElement).value); }

  generateProposal(): void {
    const topic = this.generationTopic().trim();
    if (!topic || this.generating()) return;
    this.generating.set(true); this.error.set(null);
    this.http.post<{ proposal: ProjectProposal }>(
      `/api/projects/${encodeURIComponent(this.projectName())}/proposals/generate`,
      { topic, guidance: this.generationGuidance() },
    ).pipe(finalize(() => this.generating.set(false))).subscribe({
      next: ({ proposal }) => {
        this.items.update(items => [proposal, ...items]);
        this.generationFilter.set('all'); this.statusFilter.set('all');
        this.select(proposal.id); this.generationFormOpen.set(false);
        this.generationTopic.set(''); this.generationGuidance.set('');
      },
      error: () => this.error.set('The CLI could not generate a proposal.'),
    });
  }

  beginReject(): void {
    const item = this.selected();
    if (!item) return;
    this.rejectionRaw.set(item.rejectionReasonRaw ?? '');
    this.rejectionRefined.set(item.rejectionReason ?? '');
    this.rejectionFormOpen.set(true);
  }

  refineRejection(): void {
    const feedback = this.rejectionRaw().trim();
    if (!feedback || this.refining()) return;
    this.refining.set(true); this.error.set(null);
    this.http.post<{ refinedFeedback: string }>(
      `/api/projects/${encodeURIComponent(this.projectName())}/proposals/refine-feedback`, { feedback },
    ).pipe(finalize(() => this.refining.set(false))).subscribe({
      next: result => this.rejectionRefined.set(result.refinedFeedback),
      error: () => this.error.set('The CLI could not refine the rejection feedback.'),
    });
  }

  recordRejection(): void {
    if (!this.rejectionRaw().trim() || !this.rejectionRefined().trim()) return;
    this.decide('reject', this.rejectionRefined(), this.rejectionRaw());
  }

  markThumbnailLoaded(id: string): void {
    this.loadedThumbnails.update(current => new Set(current).add(id));
  }

  evidenceUrl(proposal: ProjectProposal): string {
    const path = proposal.evidenceScreenshot.replace(/^\.\//, '');
    return `/api/projects/${encodeURIComponent(this.projectName())}/proposals/evidence/${path.split('/').map(encodeURIComponent).join('/')}`;
  }

  decide(decision: 'approve' | 'reject', rejectionReason?: string, rejectionReasonRaw?: string): void {
    const proposal = this.selected();
    if (!proposal || this.deciding()) return;
    this.deciding.set(true);
    this.error.set(null);
    this.http.post<{ proposal: ProjectProposal }>(
      `/api/projects/${encodeURIComponent(this.projectName())}/proposals/${encodeURIComponent(proposal.id)}/decision`,
      { decision, rejectionReason, rejectionReasonRaw },
    ).pipe(finalize(() => this.deciding.set(false))).subscribe({
      next: result => {
        this.items.update(items => items.map(item => item.id === result.proposal.id ? result.proposal : item));
        this.rejectionFormOpen.set(false);
        this.hydrateTasks();
      },
      error: () => this.error.set(`Could not ${decision} this proposal.`),
    });
  }

  removeSelected(): void {
    const proposal = this.selected();
    if (!proposal || this.deciding()) return;
    this.deciding.set(true);
    this.http.delete(`/api/projects/${encodeURIComponent(this.projectName())}/proposals/${encodeURIComponent(proposal.id)}`)
      .pipe(finalize(() => this.deciding.set(false))).subscribe({
        next: () => {
          this.items.update(items => items.filter(item => item.id !== proposal.id));
          this.selectedId.set(this.filteredItems()[0]?.id ?? this.items()[0]?.id ?? null);
          this.removalConfirmation.set(null);
        },
        error: () => this.error.set('Could not remove this proposal.'),
      });
  }

  removeOlder(): void {
    const keep = this.generationOptions()[0];
    if (!keep || this.deciding()) return;
    this.deciding.set(true);
    this.http.delete<{ removed: number }>(
      `/api/projects/${encodeURIComponent(this.projectName())}/proposals`, { params: { keepGeneration: keep } },
    ).pipe(finalize(() => this.deciding.set(false))).subscribe({
      next: () => { this.removalConfirmation.set(null); this.load(this.projectName()); },
      error: () => this.error.set('Could not delete older proposal generations.'),
    });
  }

  taskStatus(key: string | null): TaskReferenceStatus | null {
    return key ? this.taskStatuses().get(key.toUpperCase()) ?? null : null;
  }

  private load(project: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.http.get<{ items: ProjectProposal[] }>(`/api/projects/${encodeURIComponent(project)}/proposals`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: response => {
          this.items.set(response.items ?? []);
          const current = this.selectedId();
          if (!current || !response.items.some(p => p.id === current)) {
            this.detailImageState.set('loading');
            this.selectedId.set(response.items[0]?.id ?? null);
          }
          this.hydrateTasks();
        },
        error: () => this.error.set('Could not load project proposals.'),
      });
  }

  private hydrateTasks(): void {
    const keys = this.items().map(p => p.spawnedTask).filter((key): key is string => !!key);
    if (!keys.length) { this.taskStatuses.set(new Map()); return; }
    this.tasks.getReferenceStatuses(keys).subscribe(statuses => {
      this.taskStatuses.set(new Map(statuses.map(status => [status.key.toUpperCase(), status])));
    });
  }
}
