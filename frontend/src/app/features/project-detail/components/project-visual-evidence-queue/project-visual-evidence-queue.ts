import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { timeout } from 'rxjs';
import type { ProjectVisualEvidenceItem, ProjectVisualEvidenceQueue } from '../../../../models/project-overview.model';
import { TaskService } from '../../../../services/task.service';
import { StatusIndicatorComponent, type StatusTone } from '../../../../components/status-indicator/status-indicator.component';

/** Settled review state shown instead of the "Mark reviewed" button (AGT-3403). */
export function evidenceReviewIndicator(item: Pick<ProjectVisualEvidenceItem, 'reviewStatus'>): { tone: StatusTone; label: string } {
  return item.reviewStatus === 'reviewed'
    ? { tone: 'success', label: 'Reviewed' }
    : { tone: 'neutral', label: 'No longer actionable' };
}

@Component({
  selector: 'app-project-visual-evidence-queue',
  standalone: true,
  imports: [StatusIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-visual-evidence-queue.html',
  styleUrl: './project-visual-evidence-queue.scss',
})
export class ProjectVisualEvidenceQueueComponent {
  private static readonly overviewItemLimit = 4;
  readonly projectName = input.required<string>();
  readonly refreshGeneration = input(0);
  readonly openTask = output<{ jobId: string; watchPath: string }>();
  private readonly tasks = inject(TaskService);
  readonly queue = signal<ProjectVisualEvidenceQueue | null>(null);
  readonly loading = signal(true);
  readonly error = signal(false);
  readonly acknowledging = signal<ReadonlySet<string>>(new Set());
  readonly reviewIndicator = evidenceReviewIndicator;
  private loadGeneration = 0;
  private queueProject: string | null = null;

  constructor() {
    effect(() => {
      this.refreshGeneration();
      this.load(this.projectName());
    });
  }

  load(project = this.projectName()): void {
    if (!project) return;
    const generation = ++this.loadGeneration;
    if (project !== this.queueProject) this.queue.set(null);
    this.loading.set(true);
    this.error.set(false);
    this.tasks.getProjectVisualEvidence(project, this.refreshGeneration() > 0).pipe(
      timeout({ first: 15_000 }),
    ).subscribe({
      next: queue => {
        if (generation !== this.loadGeneration || project !== this.projectName()) return;
        const items = queue.items.slice(0, ProjectVisualEvidenceQueueComponent.overviewItemLimit);
        this.queueProject = project;
        this.queue.set({
          ...queue,
          items,
          unseenCount: items.filter(item => item.reviewStatus === 'unseen').length,
        });
        this.loading.set(false);
      },
      error: () => {
        if (generation !== this.loadGeneration || project !== this.projectName()) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  acknowledge(item: ProjectVisualEvidenceItem): void {
    if (item.reviewStatus !== 'unseen' || this.acknowledging().has(item.id)) return;
    this.acknowledging.update(current => new Set(current).add(item.id));
    this.tasks.acknowledgeProjectVisualEvidence(this.projectName(), item.id).subscribe({
      next: reviewed => {
        this.queue.update(queue => queue ? {
          ...queue,
          unseenCount: Math.max(0, queue.unseenCount - 1),
          items: queue.items.map(current => current.id === reviewed.id ? reviewed : current),
        } : queue);
        this.clearBusy(item.id);
      },
      error: () => { this.clearBusy(item.id); this.load(); },
    });
  }

  inspect(item: ProjectVisualEvidenceItem): void {
    this.openTask.emit({ jobId: item.jobId, watchPath: item.watchPath });
  }

  private clearBusy(id: string): void {
    this.acknowledging.update(current => {
      const next = new Set(current);
      next.delete(id);
      return next;
    });
  }
}
